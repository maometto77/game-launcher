using System.Net.Http;
using System.Runtime.CompilerServices;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sources.SharedCatalog;

/// <summary>
/// Populates the catalogue from a shared feed the user points at.
/// </summary>
/// <remarks>
/// <para>
/// The other sources read somebody else's website. This one reads a document
/// somebody published on purpose, usually alongside the files it describes — a
/// group hosting a catalogue for itself, so that everyone's launcher shows the
/// same shelf.
/// </para>
/// <para>
/// That difference is why this source can do something none of the others can:
/// its publisher holds the files, so the digests in the feed are ones they
/// computed rather than ones they repeated. A download from here is verified
/// against a SHA-256 by the existing download path, with nothing else to wire up.
/// </para>
/// <para>
/// The whole catalogue is one document. Enumerating fetches it once and holds
/// the parsed result, and <see cref="FetchAsync"/> is answered from that rather
/// than making a request per entry — which for a four-hundred-entry feed would
/// be four hundred requests to re-read a file already in memory.
/// </para>
/// </remarks>
public sealed class SharedCatalogSource : ICatalogSource
{
    /// <summary>Dispatch key stored on every row this source contributes.</summary>
    public const string SourceKey = "shared-catalog";

    /// <summary>Name of the configured <see cref="HttpClient"/> used for feed requests.</summary>
    public const string HttpClientName = "discovery-shared-catalog";

    /// <summary>
    /// How long a fetched document is reused before being fetched again.
    /// </summary>
    /// <remarks>
    /// Long enough that one import pass makes one request, short enough that a
    /// republished feed is picked up by the next pass rather than at restart.
    /// </remarks>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly IRobotsPolicy _robots;
    private readonly ILogger<SharedCatalogSource> _logger;

    /// <summary>Serialises fetches, so concurrent callers share one request.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Uri? _cachedUrl;
    private SharedCatalogParseResult? _cached;
    private DateTimeOffset _cachedAt;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured feed client.</param>
    /// <param name="settings">Supplies the feed address.</param>
    /// <param name="robots">Checks the host's published rules before fetching.</param>
    /// <param name="logger">Logger for source diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SharedCatalogSource(
        IHttpClientFactory httpClientFactory,
        ISettingsService settings,
        IRobotsPolicy robots,
        ILogger<SharedCatalogSource> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _robots = robots ?? throw new ArgumentNullException(nameof(robots));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => SourceKey;

    /// <inheritdoc />
    public string DisplayName => _cached?.Name ?? "Shared catalogue";

    /// <summary>
    /// Ranked above every other source.
    /// </summary>
    /// <remarks>
    /// Negative so that adding this did not mean renumbering the sources that
    /// were already here. It wins ties because it is the only source describing
    /// files its publisher actually holds: everything else reports what some
    /// other site said, and a curated shelf is a deliberate statement where a
    /// scrape is an inference. Per-field rules still come first; a rank only
    /// settles what they leave tied.
    /// </remarks>
    public int Rank => -1;

    /// <summary>
    /// Four concurrent requests, no enforced spacing.
    /// </summary>
    /// <remarks>
    /// Nearly moot, since a whole import is one request and every fetch after it
    /// is served from memory. Configured for the case where a feed is
    /// republished mid-pass and the document is fetched again.
    /// </remarks>
    public SourceThrottle Throttle { get; } = new(4, TimeSpan.Zero);

    /// <inheritdoc />
    public bool IsAvailable => FeedUrl is not null;

    /// <summary>Gets the configured feed address, or <see langword="null"/> when unusable.</summary>
    /// <remarks>
    /// Anything that is not an absolute http or https address is treated as not
    /// configured. A typo should leave the source quietly unavailable, exactly as
    /// an empty setting does, rather than throwing during an import that has
    /// other sources to get on with.
    /// </remarks>
    private Uri? FeedUrl
    {
        get
        {
            var configured = _settings.Current.SharedCatalogUrl;

            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var url))
            {
                return null;
            }

            return url.Scheme is "http" or "https" ? url : null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceListingRef> EnumerateAsync(
        SourceEnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var url = FeedUrl;

        if (url is null)
        {
            yield break;
        }

        var feed = await LoadAsync(url, refresh: true, cancellationToken).ConfigureAwait(false);

        if (feed is null)
        {
            yield break;
        }

        var yielded = 0;

        foreach (var listing in feed.Listings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The feed is a whole document with no paging, so there is nothing
            // to resume from. Cursors exist for sources that page; saying so with
            // a null is more honest than inventing a token that means nothing.
            if (options.ChangedSince is { } since
                && listing.SourceUpdatedAt is { } updated
                && updated <= since)
            {
                continue;
            }

            yield return new SourceListingRef(
                SourceKey,
                listing.SourceItemId,
                listing.Title,
                listing.SourceUpdatedAt,
                Cursor: null);

            if (options.MaxItems > 0 && ++yielded >= options.MaxItems)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public async Task<SourceListing?> FetchAsync(
        SourceListingRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var url = FeedUrl;

        if (url is null)
        {
            return null;
        }

        var feed = await LoadAsync(url, refresh: false, cancellationToken).ConfigureAwait(false);

        // Null rather than an exception when the entry has gone: the feed being
        // republished without it mid-import is the publisher removing a game,
        // which is a reason to skip the item permanently, not to retry it.
        return feed?.Listings.FirstOrDefault(listing =>
            string.Equals(listing.SourceItemId, reference.SourceItemId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the parsed feed, fetching it when the held copy is missing, stale
    /// or for a different address.
    /// </summary>
    /// <param name="url">The feed address.</param>
    /// <param name="refresh">Whether to fetch even if the held copy is fresh.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The parsed feed, or <see langword="null"/> when it could not be read.</returns>
    private async Task<SharedCatalogParseResult?> LoadAsync(
        Uri url,
        bool refresh,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var fresh = _cached is not null
                && _cachedUrl == url
                && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime;

            if (fresh && !refresh)
            {
                return _cached;
            }

            if (!await _robots.IsAllowedAsync(url, cancellationToken).ConfigureAwait(false))
            {
                // Trivially under the publisher's own control, so this almost
                // never fires for a feed someone meant to publish. It fires when
                // the setting points at a third party's document, which is
                // exactly when it should.
                _logger.LogWarning("The shared catalogue at {Url} is disallowed by that host's robots.txt.", url);
                return null;
            }

            var client = _httpClientFactory.CreateClient(HttpClientName);

            string json;

            try
            {
                json = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                // Logged and swallowed rather than thrown. An unreachable feed
                // should cost the user this source for this pass, not the import
                // that the other sources are halfway through.
                _logger.LogWarning(exception, "The shared catalogue at {Url} could not be fetched.", url);
                return null;
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "The shared catalogue at {Url} timed out.", url);
                return null;
            }

            SharedCatalogParseResult result;

            try
            {
                result = SharedCatalogParser.Parse(json, url, SourceKey);
            }
            catch (SharedCatalogFormatException exception)
            {
                // Warning rather than debug, and the message says what is wrong
                // with the document: this is nearly always a URL pointing at the
                // wrong thing, and the user is the only one who can fix it.
                _logger.LogWarning(exception, "The shared catalogue at {Url} is not a feed this build can read.", url);
                return null;
            }

            foreach (var warning in result.Warnings)
            {
                _logger.LogWarning("Shared catalogue at {Url}: {Warning}", url, warning);
            }

            _logger.LogInformation(
                "Read {Count} entries from the shared catalogue at {Url}{Skipped}.",
                result.Listings.Count,
                url,
                result.Warnings.Count > 0 ? $", skipping {result.Warnings.Count}" : string.Empty);

            _cached = result;
            _cachedUrl = url;
            _cachedAt = DateTimeOffset.UtcNow;

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
