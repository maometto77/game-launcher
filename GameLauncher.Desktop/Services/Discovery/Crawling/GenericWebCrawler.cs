using System.Runtime.CompilerServices;
using GameLauncher.Desktop.Services.Discovery.Import;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// What a crawl was asked to do.
/// </summary>
/// <param name="StartAddress">The listing page to begin at.</param>
/// <param name="Selectors">Selector overrides, possibly empty.</param>
/// <param name="Limits">The bounds to run inside.</param>
/// <param name="Policy">What addresses are acceptable.</param>
public sealed record CrawlRequest(
    Uri StartAddress,
    CrawlSelectors Selectors,
    CrawlLimits Limits,
    UrlPolicy Policy)
{
    /// <summary>
    /// Where to resume from, or <see langword="null"/> to start at the beginning.
    /// </summary>
    /// <remarks>
    /// The address of a listing page a previous pass had reached but not
    /// finished. Resuming replays that page rather than the one after it, which
    /// the import pipeline's content hashes make nearly free — whereas resuming
    /// one page too late would silently skip everything on it.
    /// </remarks>
    public string? Cursor { get; init; }

    /// <summary>A search term to match titles against, or <see langword="null"/>.</summary>
    public string? Query { get; init; }
}

/// <summary>
/// One game a crawl found, before its detail page has been read.
/// </summary>
/// <param name="SourceId">Stable identity within the site.</param>
/// <param name="Title">The title the listing showed.</param>
/// <param name="DetailAddress">The page describing it.</param>
/// <param name="Cursor">The listing page this was found on.</param>
public sealed record CrawledItem(string SourceId, string Title, Uri DetailAddress, string Cursor);

/// <summary>
/// Walks a site's listing pages and reports the games on them.
/// </summary>
/// <remarks>
/// <para>
/// The engine, and only the engine: it decides what to fetch, in what order, how
/// often, and when to stop. It does not know what a game is, does not parse a
/// detail page, and never downloads a file. Those are the parser's job, the
/// source's job and the download stack's job, and keeping them apart is what
/// makes each testable without a network.
/// </para>
/// <para>
/// Every loop here is bounded. A crawler follows links written by someone else,
/// so "the site will stop eventually" is not a design: a paginator that always
/// offers a next page, a page that links to itself, and a category tree that
/// reaches the whole domain are all ordinary bugs on ordinary sites, and each of
/// them runs forever unless something here refuses to.
/// </para>
/// </remarks>
public sealed class GenericWebCrawler
{
    private readonly PageFetcher _fetcher;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="fetcher">Reads pages, politely.</param>
    /// <param name="logger">Logger for crawl diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GenericWebCrawler(PageFetcher fetcher, ILogger logger)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Walks a site, yielding items as they are discovered.
    /// </summary>
    /// <param name="request">What to crawl.</param>
    /// <param name="diagnostics">Where to record what happened.</param>
    /// <param name="cancellationToken">Cancels the crawl.</param>
    /// <returns>The items found, in the order the site listed them.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Streamed rather than collected. A site with a few thousand games would
    /// otherwise have to be walked to the end before the first item could be
    /// imported, and a crawl interrupted at page ninety would lose all of it.
    /// </remarks>
    public async IAsyncEnumerable<CrawledItem> CrawlAsync(
        CrawlRequest request,
        CrawlDiagnostics diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var limits = request.Limits.Normalized();

        // Confined to the site it was pointed at unless the manifest widened it.
        // One page of outbound links would otherwise turn one configured source
        // into a walk of the open web.
        var policy = request.Policy.AllowedHosts.Count > 0
            ? request.Policy
            : request.Policy.ConfinedTo(request.StartAddress.Host);

        using var throttle = new RequestThrottle(new SourceThrottle(limits.Concurrency, limits.Delay));

        var start = UrlGuard.Canonicalize(request.Cursor, request.StartAddress) ?? request.StartAddress;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Uri? next = start;
        var pages = 0;
        var items = 0;
        var barrenPages = 0;

        while (next is not null && pages < limits.MaxPages && items < limits.MaxItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A paginator pointing back into pages already walked is the usual
            // shape of endless pagination. Stopping here rather than at MaxPages
            // turns a wasted hundred requests into none.
            if (!visited.Add(next.AbsoluteUri))
            {
                _logger.LogDebug("Crawl stopped: {Address} has already been walked.", next);
                break;
            }

            var current = next;

            using var result = await throttle
                .ExecuteAsync(
                    token => _fetcher.FetchAsync(current, policy, limits, diagnostics, token),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsOk)
            {
                // A listing page that cannot be read ends the walk: there is no
                // next link to follow out of a page we do not have.
                _logger.LogWarning(
                    "Crawl stopped at {Address}: {Reason}.", current, result.Explanation ?? "unreadable");

                break;
            }

            pages++;

            var listing = ListingPageParser.Parse(result.Page!, request.Selectors, policy, limits, diagnostics);

            if (listing.Entries.Count == 0)
            {
                barrenPages++;

                diagnostics.PageFailed(current.AbsoluteUri, "no entries recognised");

                _logger.LogDebug(
                    "Nothing recognised on {Address} ({Count} in a row).", current, barrenPages);

                if (barrenPages >= limits.MaxConsecutiveFailures)
                {
                    // The signal that a site has been redesigned under a working
                    // set of selectors. Continuing would import nothing and
                    // report success.
                    _logger.LogWarning(
                        "Crawl abandoned after {Count} page(s) with nothing recognisable on them.", barrenPages);

                    break;
                }
            }
            else
            {
                barrenPages = 0;
            }

            foreach (var entry in listing.Entries)
            {
                if (items >= limits.MaxItems)
                {
                    break;
                }

                if (!emitted.Add(entry.DetailAddress.AbsoluteUri))
                {
                    // The same game linked from two listing pages, which happens
                    // whenever a site paginates a feed still being written to.
                    diagnostics.DuplicateSkipped();
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Title))
                {
                    // A title is the one field the catalogue cannot do without:
                    // it is what listings are matched on.
                    diagnostics.ItemSkipped();
                    continue;
                }

                if (!Matches(request.Query, entry.Title))
                {
                    continue;
                }

                items++;
                diagnostics.ItemFound();

                yield return new CrawledItem(
                    CrawlIdentity.FromAddress(entry.DetailAddress),
                    entry.Title,
                    entry.DetailAddress,

                    // The page this was found on, not the one after it, so a
                    // resumed pass replays rather than skips.
                    current.AbsoluteUri);
            }

            next = listing.NextPage;
        }

        _logger.LogInformation("Crawl of {Address} finished: {Summary}.", start, diagnostics.Summarize());
    }

    /// <summary>
    /// Determines whether a title matches a search term.
    /// </summary>
    /// <param name="query">The term, or <see langword="null"/> for everything.</param>
    /// <param name="title">The title to test.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    /// <remarks>
    /// Applied here rather than in the request, because a site's listing pages
    /// are documents and not a query endpoint. A search against a crawled source
    /// therefore costs about what a pass over it costs, which is why the caller
    /// caps how much of it a search may read.
    /// </remarks>
    private static bool Matches(string? query, string title) =>
        string.IsNullOrWhiteSpace(query) ||
        title.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>
/// Works out a stable identity for a crawled page.
/// </summary>
/// <remarks>
/// The source identifier has to survive a re-crawl, because it is what the
/// import pipeline uses to recognise an item it has already stored. A site's own
/// numeric id would be better, and most sites do not publish one anywhere a
/// crawler can see, so the address is used instead: it is what a person would
/// use to say which game they meant.
/// </remarks>
public static class CrawlIdentity
{
    /// <summary>
    /// Builds an identifier from a detail page address.
    /// </summary>
    /// <param name="address">The page describing one game.</param>
    /// <returns>An identifier stable across crawls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Host and path, without the scheme or a trailing slash. The scheme is left
    /// out so that a site moving to HTTPS does not duplicate its whole
    /// catalogue, and the query is kept because plenty of sites identify a page
    /// entirely by one.
    /// </remarks>
    public static string FromAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var path = address.AbsolutePath.TrimEnd('/');

        if (path.Length == 0)
        {
            path = "/";
        }

        return $"{address.Host}{path}{address.Query}";
    }
}
