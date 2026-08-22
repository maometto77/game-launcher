using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using GameLauncher.Desktop.Services.Discovery.Crawling;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Html;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sources.Crawler;

/// <summary>
/// Fills the catalogue by crawling the sites named in the adapter manifests.
/// </summary>
/// <remarks>
/// <para>
/// The third way a manifest can describe a catalogue, and the one that needs
/// least of the site: a starting address. A feed has to be published; a script
/// has to be written; a page merely has to exist. Most sites worth indexing have
/// never published a feed and never will.
/// </para>
/// <para>
/// One source for every crawling manifest rather than one source per manifest,
/// for the same reason the feed sources work that way: sources are a fixed set
/// resolved from the container, and manifests are files that appear and vanish
/// while the application runs. Each observation still carries its own manifest's
/// key, so a game found by crawling one site is attributed to that site.
/// </para>
/// <para>
/// It crawls and it describes. It does not resolve a download address, by
/// design — see the sourcing adapters for that, and see
/// <c>docs/generic-crawler.md</c> for why the two halves are separate.
/// </para>
/// </remarks>
public sealed class CrawlerCatalogSource : ICatalogSource
{
    /// <summary>Dispatch key for the family.</summary>
    public const string SourceKey = "crawled-sites";

    /// <summary>Separates a manifest's key from the site's own item id.</summary>
    private const char IdSeparator = '|';

    private readonly IFeedManifestStore _manifests;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRobotsPolicy _robots;
    private readonly ISettingsService _settings;
    private readonly Infrastructure.IAppPaths _paths;
    private readonly ILogger<CrawlerCatalogSource> _logger;

    /// <summary>
    /// Items found during the last enumeration, answering the fetches that follow.
    /// </summary>
    /// <remarks>
    /// The enumeration knows each item's address and the manifest it came from;
    /// the pipeline then asks for them one at a time, by identifier alone. This
    /// is how the second call finds its way back to the first, and it is a
    /// dictionary rather than a re-crawl because re-walking the listing pages to
    /// answer "what was item 400" would multiply the crawl by its own length.
    /// </remarks>
    private readonly ConcurrentDictionary<string, PendingItem> _pending = new(StringComparer.Ordinal);

    /// <summary>Diagnostics from the most recent pass, for reporting.</summary>
    private volatile CrawlDiagnostics _diagnostics = new();

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="manifests">Supplies the user's manifests.</param>
    /// <param name="httpClientFactory">Supplies the configured crawl client.</param>
    /// <param name="robots">Checks each site's published rules before fetching.</param>
    /// <param name="settings">Says whether discovery is switched on at all.</param>
    /// <param name="paths">Supplies the adapter directory.</param>
    /// <param name="logger">Logger for crawl diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CrawlerCatalogSource(
        IFeedManifestStore manifests,
        IHttpClientFactory httpClientFactory,
        IRobotsPolicy robots,
        ISettingsService settings,
        Infrastructure.IAppPaths paths,
        ILogger<CrawlerCatalogSource> logger)
    {
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _robots = robots ?? throw new ArgumentNullException(nameof(robots));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => SourceKey;

    /// <inheritdoc />
    public string DisplayName => "Crawled sites";

    /// <summary>
    /// Ranked behind the built-in sources when they disagree about a field.
    /// </summary>
    /// <remarks>
    /// Not a judgement about any particular site: it is that a field read out of
    /// prose by a heuristic is a weaker claim than the same field out of a
    /// curated database. A tie is rare and the per-field rules settle almost
    /// everything before this is consulted.
    /// </remarks>
    public int Rank => 20;

    /// <summary>
    /// One request at a time, spaced by a second.
    /// </summary>
    /// <remarks>
    /// The conservative default, and the manifest's own <c>delayMilliseconds</c>
    /// and <c>concurrency</c> apply on top of it inside the crawl. These are
    /// sites this launcher knows nothing about, and a crawler is the component
    /// most able to be a nuisance.
    /// </remarks>
    public SourceThrottle Throttle => new(1, TimeSpan.FromSeconds(1));

    /// <summary>Gets what the most recent pass did, for reporting.</summary>
    public CrawlDiagnostics LastDiagnostics => _diagnostics;

    /// <summary>
    /// Available once discovery is on and a manifest declares a crawl.
    /// </summary>
    /// <remarks>
    /// Falls back to a directory probe before the first load, for the same
    /// reason the feed source does: answering "no" until something else had read
    /// the folder would make the very first import after a restart skip every
    /// crawling manifest, which is exactly when someone is trying it.
    /// </remarks>
    public bool IsAvailable =>
        _settings.Current.DiscoveryEnabled &&
        (_manifests.Cached is { } cached
            ? cached.Any(manifest => manifest.ProvidesCrawler)
            : HasAnyManifestFile());

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceListingRef> EnumerateAsync(
        SourceEnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var manifests = await _manifests.GetAsync(cancellationToken).ConfigureAwait(false);
        var crawling = manifests.Where(manifest => manifest.ProvidesCrawler).ToArray();

        if (crawling.Length == 0)
        {
            yield break;
        }

        var diagnostics = new CrawlDiagnostics();
        _diagnostics = diagnostics;
        _pending.Clear();

        var fetcher = new PageFetcher(new NamedHttpClientFactory(_httpClientFactory, "discovery-custom-feeds"), _robots, _logger);
        var crawler = new GenericWebCrawler(fetcher, _logger);
        var yielded = 0;

        foreach (var manifest in crawling)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var settings = manifest.Crawler!;

            if (UrlGuard.Canonicalize(settings.Url) is not { } start)
            {
                _logger.LogWarning(
                    "Crawl for '{Key}' skipped: '{Url}' is not a usable address.", manifest.Key, settings.Url);

                continue;
            }

            var limits = settings.ToLimits();

            // A search reads far less of the site than a full pass, because a
            // listing page is a document and not a query endpoint: narrowing it
            // costs a walk either way, so the walk is kept short.
            if (!string.IsNullOrWhiteSpace(options.Query))
            {
                limits = limits with { MaxPages = Math.Min(limits.MaxPages, 10) };
            }

            if (options.MaxItems > 0)
            {
                limits = limits with { MaxItems = Math.Min(limits.MaxItems, options.MaxItems) };
            }

            var request = new CrawlRequest(start, settings.Selectors, limits, settings.ToPolicy())
            {
                // Resumes the listing page a killed pass had reached. Only the
                // first manifest can resume, because a cursor is one address and
                // says nothing about which site it belongs to.
                Cursor = crawling.Length == 1 ? options.Cursor : null,
                Query = options.Query
            };

            await foreach (var item in crawler
                               .CrawlAsync(request, diagnostics, cancellationToken)
                               .ConfigureAwait(false))
            {
                var identity = $"{manifest.Key}{IdSeparator}{item.SourceId}";

                _pending[identity] = new PendingItem(manifest, item);

                yield return new SourceListingRef(
                    SourceKey,
                    identity,
                    item.Title,

                    // A listing page rarely dates its entries, and guessing would
                    // be worse than saying nothing: a wrong stamp makes an
                    // incremental pass skip an item that had in fact changed.
                    null,
                    item.Cursor);

                if (options.MaxItems > 0 && ++yielded >= options.MaxItems)
                {
                    yield break;
                }
            }
        }

        _logger.LogInformation(
            "Crawled {Count} site(s): {Summary}.", crawling.Length, diagnostics.Summarize());
    }

    /// <inheritdoc />
    public async Task<SourceListing?> FetchAsync(
        SourceListingRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!_pending.TryGetValue(reference.SourceItemId, out var pending))
        {
            // An item absent from the enumeration that produced this reference.
            // Ordinary rather than exceptional: it means skip, not retry.
            return null;
        }

        var manifest = pending.Manifest;
        var settings = manifest.Crawler!;
        var policy = settings.ToPolicy();

        if (!settings.ReadDetailPages)
        {
            // Title and address only. Thin, and enough to be installable when a
            // sourcing adapter claims the address — which is the point of being
            // able to turn detail reads off on a large site.
            return Sparse(pending, manifest.Key);
        }

        var fetcher = new PageFetcher(new NamedHttpClientFactory(_httpClientFactory, "discovery-custom-feeds"), _robots, _logger);
        var diagnostics = _diagnostics;

        using var result = await fetcher
            .FetchAsync(pending.Item.DetailAddress, policy, settings.ToLimits(), diagnostics, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsOk)
        {
            _logger.LogDebug(
                "Detail page {Address} could not be read: {Reason}.",
                pending.Item.DetailAddress, result.Explanation ?? "unreadable");

            // The listing page already told us the title, so a page that cannot
            // be read costs description and artwork rather than the whole game.
            return Sparse(pending, manifest.Key);
        }

        var listing = DetailPageReader.Read(
            result.Page!, pending.Item, settings.Selectors, manifest.Key, policy);

        return Enrich(listing, result.Page!, manifest, diagnostics);
    }

    /// <summary>
    /// Adds download addresses during the import, when the manifest asked for it.
    /// </summary>
    /// <param name="listing">The observation read from the page.</param>
    /// <param name="page">The page it was read from.</param>
    /// <param name="manifest">The manifest being imported.</param>
    /// <param name="diagnostics">Where to record refused addresses.</param>
    /// <returns>The observation, with addresses when they were wanted and found.</returns>
    /// <remarks>
    /// <para>
    /// Only for <c>resolution: eager</c>, and only for the <c>direct-link</c>
    /// strategy — where it is nearly free, because the page the addresses are on
    /// is the page just parsed. No extra request is made.
    /// </para>
    /// <para>
    /// The other two strategies are left to install time even when a manifest
    /// asks for eager. A script would have to be spawned once per game, and a
    /// mapped field is read from a catalogue that this import is in the middle of
    /// writing; both would make the import pay a large cost to answer a question
    /// about games nobody has asked to install. Lazy is the default for exactly
    /// this reason and remains the fallback when eager cannot be honoured
    /// cheaply.
    /// </para>
    /// </remarks>
    private SourceListing Enrich(
        SourceListing listing,
        CrawledPage page,
        FeedManifest manifest,
        CrawlDiagnostics diagnostics)
    {
        if (manifest.Sourcing is not { Enabled: true, Resolution: SourcingResolution.Eager } sourcing)
        {
            return listing;
        }

        if (sourcing.Strategy != SourcingStrategy.DirectLink)
        {
            _logger.LogDebug(
                "Source '{Key}' asked for eager resolution with the {Strategy} strategy, " +
                "which is left to install time.",
                manifest.Key, sourcing.Strategy);

            return listing;
        }

        var candidates = DirectLinkExtractor.Extract(page, sourcing, diagnostics);

        if (candidates.Count == 0)
        {
            return listing;
        }

        var downloads = candidates
            .OrderByDescending(candidate => candidate.Priority)
            .Select((candidate, index) => new ListingDownloadRef
            {
                Url = candidate.Address,
                FileName = candidate.FileName,
                SizeBytes = candidate.SizeBytes,
                Sha256 = candidate.Sha256,
                Sha1 = candidate.Sha1,
                Md5 = candidate.Md5,
                Format = candidate.Format,
                Kind = candidate.Kind,
                MirrorRank = index
            })
            .ToArray();

        _logger.LogDebug(
            "Resolved {Count} address(es) for '{Title}' during the import.",
            downloads.Length, listing.Title);

        return listing with { Downloads = downloads };
    }

    /// <summary>
    /// Builds an observation from what the listing page alone said.
    /// </summary>
    /// <param name="pending">The item.</param>
    /// <param name="sourceKey">The manifest's key.</param>
    /// <returns>The observation.</returns>
    private static SourceListing Sparse(PendingItem pending, string sourceKey) => new()
    {
        SourceKey = sourceKey,
        SourceItemId = pending.Item.SourceId,
        SourceUrl = pending.Item.DetailAddress,
        Title = pending.Item.Title,
        IsDownloadable = true,
        RawPayload = "{}"
    };

    /// <summary>
    /// Determines whether the adapter folder holds anything worth loading.
    /// </summary>
    /// <returns><see langword="true"/> when it does.</returns>
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

    /// <summary>One discovered item, waiting to be fetched.</summary>
    /// <param name="Manifest">The manifest that found it.</param>
    /// <param name="Item">What the listing page said.</param>
    private sealed record PendingItem(FeedManifest Manifest, CrawledItem Item);
    /// <summary>
    /// Delegates client creation to IHttpClientFactory using a fixed named client config.
    /// </summary>
    private sealed class NamedHttpClientFactory : IHttpClientFactory
    {
        private readonly IHttpClientFactory _inner;
        private readonly string _clientName;

        public NamedHttpClientFactory(IHttpClientFactory inner, string clientName)
        {
            _inner = inner;
            _clientName = clientName;
        }

        public HttpClient CreateClient(string name) => _inner.CreateClient(_clientName);
    }
}
