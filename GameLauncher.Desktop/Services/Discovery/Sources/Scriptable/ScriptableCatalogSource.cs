using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sources.Scriptable;

/// <summary>
/// Fills the catalogue from the manifests in the adapter directory.
/// </summary>
/// <remarks>
/// <para>
/// The other half of a feed manifest. <see cref="ScriptableSourcingAdapter"/>
/// answers "given this listing, what can be downloaded"; this answers "what
/// games are there". A manifest with only the former is inert until some other
/// source has already put listings in the catalogue for it to resolve — which is
/// the single most confusing thing about writing one, and the reason this
/// exists.
/// </para>
/// <para>
/// One source for every manifest rather than one per manifest, because sources
/// are a fixed set resolved from the container and manifests are files that
/// appear and vanish while the application runs. The individual feed's key still
/// lands on each observation, so a listing found through a custom feed is
/// attributed to that feed and not to this class.
/// </para>
/// <para>
/// Like everything else here it obeys <c>robots.txt</c>, and it fetches nothing
/// until discovery has been switched on.
/// </para>
/// </remarks>
public sealed class ScriptableCatalogSource : ICatalogSource
{
    /// <summary>Dispatch key for the family.</summary>
    public const string SourceKey = "custom-feeds";

    /// <summary>Name of the configured <see cref="HttpClient"/> used for feed requests.</summary>
    public const string HttpClientName = "discovery-custom-feeds";

    /// <summary>Separates a manifest's key from the feed's own item id.</summary>
    /// <remarks>
    /// A composite identifier, because the pipeline hands back only what this
    /// source put in <see cref="SourceListingRef.SourceItemId"/> and the fetch
    /// has to know which manifest an item came from. A vertical bar cannot occur
    /// in a manifest key, which is a file-name-shaped thing.
    /// </remarks>
    private const char IdSeparator = '|';

    private readonly IFeedManifestStore _manifests;
    private readonly IScriptHookRunner _hooks;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRobotsPolicy _robots;
    private readonly Settings.ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<ScriptableCatalogSource> _logger;

    /// <summary>
    /// Items mapped during the last enumeration, answering the fetch that follows.
    /// </summary>
    /// <remarks>
    /// A feed returns every item in one document, so the metadata is already in
    /// hand by the time the pipeline asks for it item by item. Re-requesting the
    /// same document once per item would turn one request into several hundred,
    /// against a host that published everything in the first response.
    /// </remarks>
    private readonly Dictionary<string, SourceListing> _mapped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="manifests">Supplies the user's feed manifests.</param>
    /// <param name="hooks">Runs a manifest's transform program, when it has one.</param>
    /// <param name="httpClientFactory">Supplies the configured feed client.</param>
    /// <param name="robots">Checks each site's published rules before fetching.</param>
    /// <param name="settings">Says whether discovery is switched on at all.</param>
    /// <param name="paths">Supplies the adapter directory.</param>
    /// <param name="logger">Logger for source diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ScriptableCatalogSource(
        IFeedManifestStore manifests,
        IScriptHookRunner hooks,
        IHttpClientFactory httpClientFactory,
        IRobotsPolicy robots,
        Settings.ISettingsService settings,
        IAppPaths paths,
        ILogger<ScriptableCatalogSource> logger)
    {
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _robots = robots ?? throw new ArgumentNullException(nameof(robots));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => SourceKey;

    /// <inheritdoc />
    public string DisplayName => "Custom feeds";

    /// <summary>
    /// Ranked behind the built-in sources when they disagree about a field.
    /// </summary>
    /// <remarks>
    /// Not a judgement about the feed: it is that the built-in sources read
    /// curated databases with structured fields, and a hand-written manifest
    /// usually maps whatever a site happened to publish. A tie is rare and the
    /// per-field rules decide almost everything before this is consulted.
    /// </remarks>
    public int Rank => 10;

    /// <summary>
    /// One request at a time, spaced.
    /// </summary>
    /// <remarks>
    /// These are hosts this launcher knows nothing about, and a manifest can
    /// name any of them. The conservative default is the only honest choice
    /// when the site might be a hobby server on a home connection.
    /// </remarks>
    public SourceThrottle Throttle => SourceThrottle.Polite;

    /// <summary>
    /// Available once discovery is on and there is a manifest that might declare
    /// a catalogue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous by interface, and reading the manifest folder is not. Already
    /// loaded manifests give the exact answer; before the first load it falls
    /// back to asking whether the adapter folder holds anything at all.
    /// </para>
    /// <para>
    /// That fallback is the whole point. Answering "no" until something else had
    /// loaded the manifests would mean the very first import after a restart
    /// skipped every custom feed — and since importing is the thing that fills
    /// an empty catalogue, the feature would appear not to work at precisely the
    /// moment someone was trying it. A wrong "yes" costs one enumeration that
    /// yields nothing.
    /// </para>
    /// </remarks>
    public bool IsAvailable =>
        _settings.Current.DiscoveryEnabled &&
        (_manifests.Cached is { } cached
            ? cached.Any(manifest => manifest.ProvidesCatalog)
            : HasAnyManifestFile());

    /// <summary>
    /// Determines whether the adapter folder holds anything worth loading.
    /// </summary>
    /// <returns><see langword="true"/> when it does.</returns>
    /// <remarks>
    /// A directory listing rather than a parse: this only has to be right about
    /// whether an enumeration is worth attempting, and the enumeration itself
    /// decides what is actually there.
    /// </remarks>
    private bool HasAnyManifestFile()
    {
        try
        {
            return Directory.Exists(_paths.AdapterDirectory) &&
                   new[] { "*.yaml", "*.yml", "*.json" }.Any(pattern =>
                       Directory.EnumerateFiles(_paths.AdapterDirectory, pattern).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not inspect the adapter directory.");
            return false;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceListingRef> EnumerateAsync(
        SourceEnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var manifests = await _manifests.GetAsync(cancellationToken).ConfigureAwait(false);

        _mapped.Clear();

        var yielded = 0;

        foreach (var manifest in manifests.Where(candidate => candidate.ProvidesCatalog))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<SourceListing> listings;

            try
            {
                listings = await ReadAsync(manifest, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or
                                           FormatException or InvalidOperationException)
            {
                // Named, and survivable. One broken manifest must not take the
                // other feeds down with it, and these are files edited by hand.
                _logger.LogWarning(ex, "Custom feed '{Key}' could not be read.", manifest.Key);
                continue;
            }

            foreach (var listing in listings)
            {
                _mapped[listing.SourceItemId] = listing;

                yield return new SourceListingRef(
                    SourceKey,
                    listing.SourceItemId,
                    listing.Title,
                    listing.SourceUpdatedAt,

                    // No cursor. A feed is one document fetched whole, so there
                    // is no position in it worth resuming from — re-reading it
                    // costs one request and the pipeline's content hashes make
                    // the items themselves free.
                    null);

                if (options.MaxItems > 0 && ++yielded >= options.MaxItems)
                {
                    yield break;
                }
            }
        }
    }

    /// <inheritdoc />
    public Task<SourceListing?> FetchAsync(
        SourceListingRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        // Answered from the enumeration that produced the reference. An item
        // absent from it is one whose feed changed underneath the pass, which is
        // an ordinary outcome and means "skip", not "retry".
        return Task.FromResult(_mapped.GetValueOrDefault(reference.SourceItemId));
    }

    /// <summary>
    /// Fetches and maps one manifest's catalogue.
    /// </summary>
    /// <param name="manifest">The manifest to read.</param>
    /// <param name="options">Narrows the pass.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The listings it described.</returns>
    private async Task<IReadOnlyList<SourceListing>> ReadAsync(
        FeedManifest manifest,
        SourceEnumerationOptions options,
        CancellationToken cancellationToken)
    {
        var catalog = manifest.Catalog!;
        var text = await FetchTextAsync(manifest, catalog, cancellationToken).ConfigureAwait(false);

        if (text is null)
        {
            return [];
        }

        var format = catalog.Format;

        if (catalog.Transform is { } transform)
        {
            text = await _hooks
                .RunAsync(transform, text, Path.GetDirectoryName(manifest.SourcePath) ?? ".", cancellationToken)
                .ConfigureAwait(false);

            // A hook's contract is JSON out, whatever went in.
            format = FeedFormat.Json;
        }

        var payload = FeedReader.Read(text, format);
        var listings = new List<SourceListing>();

        foreach (var item in payload.ListAt(catalog.Items))
        {
            if (Map(manifest, catalog, item, options.Query) is { } listing)
            {
                listings.Add(listing);
            }
        }

        _logger.LogInformation(
            "Custom feed '{Key}' described {Count} game(s).", manifest.Key, listings.Count);

        return listings;
    }

    /// <summary>
    /// Reads a manifest's catalogue payload, from the network or from disk.
    /// </summary>
    /// <param name="manifest">The manifest being read.</param>
    /// <param name="catalog">Its catalogue section.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The payload, or <see langword="null"/> when the site's rules forbid it.</returns>
    /// <exception cref="InvalidOperationException">A local path escapes the adapter directory, or is missing.</exception>
    private async Task<string?> FetchTextAsync(
        FeedManifest manifest,
        FeedCatalog catalog,
        CancellationToken cancellationToken)
    {
        var request = catalog.Request.Url;

        if (!Uri.TryCreate(request, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            return await ReadLocalAsync(manifest, request, cancellationToken).ConfigureAwait(false);
        }

        // The same gate every other network read passes through. An extension
        // point that quietly skipped it would be the first thing anyone used to
        // get around a decision the rest of this code takes seriously.
        if (!await _robots.IsAllowedAsync(address, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Custom feed '{Key}' is disallowed by robots.txt at {Address}.", manifest.Key, address);

            return null;
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, address);

        foreach (var (name, value) in catalog.Request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a catalogue file from the adapter directory.
    /// </summary>
    /// <param name="manifest">The manifest naming it.</param>
    /// <param name="request">The file name or relative path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file's contents.</returns>
    /// <exception cref="InvalidOperationException">The path escapes the adapter directory, or is missing.</exception>
    /// <remarks>
    /// Confined to the folder the manifest came from, for the same reason the
    /// sourcing half is: without it a manifest could name any file on the
    /// machine and have the launcher read it.
    /// </remarks>
    private static async Task<string> ReadLocalAsync(
        FeedManifest manifest,
        string request,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.GetDirectoryName(manifest.SourcePath) ?? ".");
        var file = Path.GetFullPath(Path.Combine(root, request));

        if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{request}' is outside the adapter directory, so it was not read.");
        }

        if (!File.Exists(file))
        {
            throw new InvalidOperationException($"The catalogue file '{request}' does not exist.");
        }

        return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns one payload item into an observation.
    /// </summary>
    /// <param name="manifest">The manifest being read.</param>
    /// <param name="catalog">Its catalogue section.</param>
    /// <param name="item">The item node.</param>
    /// <param name="query">A search term the pass is narrowed to, or <see langword="null"/>.</param>
    /// <returns>The observation, or <see langword="null"/> when the item is unusable.</returns>
    /// <remarks>
    /// The search term is applied here rather than in the request. A feed is a
    /// document, not a query endpoint, so narrowing it is the reader's job —
    /// which is also why a search against a custom feed costs the same as a full
    /// pass and is capped by the caller.
    /// </remarks>
    private SourceListing? Map(FeedManifest manifest, FeedCatalog catalog, FeedNode item, string? query)
    {
        var title = item.String(catalog.Map.Title);

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(query) &&
            title.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) is false)
        {
            return null;
        }

        var id = item.String(catalog.Map.Id) ?? title;

        if (BuildPage(catalog, item, id) is not { } page)
        {
            return null;
        }

        var downloads = new List<ListingDownloadRef>();

        if (item.String(catalog.Map.DownloadUrl) is { } download &&
            Uri.TryCreate(download, UriKind.Absolute, out var downloadUrl) &&
            downloadUrl.Scheme is "http" or "https" or "magnet")
        {
            downloads.Add(new ListingDownloadRef
            {
                Url = downloadUrl,
                FileName = item.String(catalog.Map.FileName),
                SizeBytes = item.Int64(catalog.Map.SizeBytes),

                // Read through the same digest filter the sourcing half uses,
                // so a feed publishing "unknown" in a checksum field is left
                // unverified rather than made to fail every transfer.
                Sha256 = FeedDownloadMapper.Digest(item.String(catalog.Map.Sha256)),
                Sha1 = FeedDownloadMapper.Digest(item.String(catalog.Map.Sha1)),
                Md5 = FeedDownloadMapper.Digest(item.String(catalog.Map.Md5)),

                Kind = downloadUrl.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ||
                       downloadUrl.Scheme == "magnet"
                    ? DownloadKind.Torrent
                    : DownloadKind.Game,
                MirrorRank = 0
            });
        }

        var images = new List<ListingImageRef>();

        if (item.String(catalog.Map.CoverUrl) is { } cover &&
            Uri.TryCreate(cover, UriKind.Absolute, out var coverUrl))
        {
            images.Add(new ListingImageRef(coverUrl, ListingImageKind.Cover, 0, 0, 0));
        }

        var published = ParseTimestamp(item.String(catalog.Map.PubDate));

        return new SourceListing
        {
            // The manifest's own key, not this class's. A game found through a
            // custom feed should be attributed to that feed on the card, which
            // is what someone reading it needs to know.
            SourceKey = manifest.Key,
            SourceItemId = $"{manifest.Key}{IdSeparator}{id}",
            SourceUrl = page,
            Title = title,

            // A mapped year wins, because it is what the feed says the game is
            // from. A publication date is only when the entry was posted, which
            // for a preservation archive is decades later — so it is a fallback
            // and never an override.
            Year = (int?)item.Int64(catalog.Map.Year) ?? published?.Year,
            Description = item.String(catalog.Map.Description),
            Developer = item.String(catalog.Map.Developer),
            Publisher = item.String(catalog.Map.Publisher),
            Images = images,
            Downloads = downloads,

            // A listing with no address of its own is still installable: the
            // sourcing adapters are asked at install time, and one of them may
            // well handle the page this points at.
            IsDownloadable = true,

            // The change stamp an incremental pass compares against. Without it
            // every import re-reads the whole feed, because nothing recorded
            // says when an entry last moved.
            SourceUpdatedAt = published,
            RawPayload = "{}"
        };
    }

    /// <summary>
    /// Reads a timestamp a feed may have written in any of several ways.
    /// </summary>
    /// <param name="value">The mapped value, or <see langword="null"/>.</param>
    /// <returns>The instant, or <see langword="null"/> when unreadable.</returns>
    /// <remarks>
    /// Round-trip and ISO-like forms first, then whatever the invariant culture
    /// accepts, which covers both spellings the Archive itself uses —
    /// <c>2026-01-17T16:44:40Z</c> and <c>2025-10-04 16:52:20</c>. A value that
    /// parses as neither is discarded rather than guessed at: a wrong date
    /// becomes a wrong year, and a wrong year makes the matcher treat a game as
    /// a different release.
    /// </remarks>
    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        // A bare year, which some feeds publish where a date belongs.
        if (text.Length == 4 &&
            int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var year) &&
            year is >= 1000 and <= 9999)
        {
            return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>
    /// Works out the address of an item's page.
    /// </summary>
    /// <param name="catalog">The catalogue section.</param>
    /// <param name="item">The item node.</param>
    /// <param name="id">The item's identifier.</param>
    /// <returns>The address, or <see langword="null"/> when there is none.</returns>
    /// <remarks>
    /// The mapped field first, then the template. Feeds that publish whole
    /// addresses and feeds that publish identifiers are both common, and only
    /// the template can turn the second into the first — the mapping language
    /// walks a payload rather than building strings.
    /// </remarks>
    private static Uri? BuildPage(FeedCatalog catalog, FeedNode item, string id)
    {
        if (item.String(catalog.Map.Page) is { } page &&
            Uri.TryCreate(page, UriKind.Absolute, out var mapped) &&
            mapped.Scheme is "http" or "https")
        {
            return mapped;
        }

        if (string.IsNullOrWhiteSpace(catalog.PageTemplate))
        {
            return null;
        }

        var built = catalog.PageTemplate.Replace(
            "{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);

        return Uri.TryCreate(built, UriKind.Absolute, out var templated) && templated.Scheme is "http" or "https"
            ? templated
            : null;
    }
}
