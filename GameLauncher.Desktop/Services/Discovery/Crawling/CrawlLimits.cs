namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// The bounds a crawl runs inside.
/// </summary>
/// <remarks>
/// <para>
/// Every field has a finite default. A crawler pointed at an unfamiliar site is
/// following links written by someone else, and the failure modes are not rare:
/// a calendar widget paginating to the end of time, a page that links to itself,
/// a category tree that reaches every document on the domain. None of those are
/// malice and all of them run forever without a limit.
/// </para>
/// <para>
/// A manifest may raise or lower any of them, but not to unlimited. The point of
/// the ceiling is that a mistake in a text file cannot start something nobody
/// can stop.
/// </para>
/// </remarks>
public sealed record CrawlLimits
{
    /// <summary>Largest page count a manifest may ask for.</summary>
    public const int MaxPagesCeiling = 10_000;

    /// <summary>Largest item count a manifest may ask for.</summary>
    public const int MaxItemsCeiling = 200_000;

    /// <summary>Largest link depth a manifest may ask for.</summary>
    public const int MaxDepthCeiling = 50;

    /// <summary>Largest concurrency a manifest may ask for.</summary>
    public const int ConcurrencyCeiling = 8;

    /// <summary>Largest response a page fetch will read, in bytes.</summary>
    public const int ResponseBytesCeiling = 16 * 1024 * 1024;

    /// <summary>How many listing pages to walk.</summary>
    public int MaxPages { get; init; } = 100;

    /// <summary>How many items to yield in total.</summary>
    public int MaxItems { get; init; } = 5_000;

    /// <summary>
    /// How far from the starting page a crawl may follow links.
    /// </summary>
    /// <remarks>
    /// Depth one is the listing pages themselves; a detail page is depth two.
    /// Deeper than that is only useful for a site that splits one game across
    /// several documents, which is rare enough to be opt-in.
    /// </remarks>
    public int MaxDepth { get; init; } = 2;

    /// <summary>How many detail pages to read at once.</summary>
    /// <remarks>
    /// One by default. A crawl of an unfamiliar site is a guest, and the site's
    /// own <c>Crawl-delay</c> is honoured on top of this.
    /// </remarks>
    public int Concurrency { get; init; } = 1;

    /// <summary>Smallest gap between the start of two requests.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait for one response.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts per page before giving up on it.</summary>
    public int Retries { get; init; } = 3;

    /// <summary>Largest response this crawl will read.</summary>
    /// <remarks>
    /// A page is a document, not a download. Reading without a bound turns one
    /// misconfigured route into an out-of-memory failure, and the existing
    /// download stack is what large files are supposed to go through.
    /// </remarks>
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// How many links to consider on any one page.
    /// </summary>
    /// <remarks>
    /// A sitemap or tag cloud can carry thousands, and a crawl that queued all
    /// of them would spend its budget on navigation rather than on games.
    /// </remarks>
    public int MaxLinksPerPage { get; init; } = 500;

    /// <summary>
    /// How many pages may fail to parse before the crawl is abandoned.
    /// </summary>
    /// <remarks>
    /// The signal that a site has been redesigned under a working set of
    /// selectors. Continuing would import nothing and report success.
    /// </remarks>
    public int MaxConsecutiveFailures { get; init; } = 5;

    /// <summary>The defaults.</summary>
    public static CrawlLimits Default { get; } = new();

    /// <summary>
    /// Clamps every field into a range the engine will honour.
    /// </summary>
    /// <returns>A usable set of limits.</returns>
    /// <remarks>
    /// Clamped rather than rejected. A manifest asking for a million pages meant
    /// "all of them", and refusing to load the file over it would help nobody;
    /// running a million page fetches would help them even less.
    /// </remarks>
    public CrawlLimits Normalized() => new()
    {
        MaxPages = Math.Clamp(MaxPages <= 0 ? MaxPagesCeiling : MaxPages, 1, MaxPagesCeiling),
        MaxItems = Math.Clamp(MaxItems <= 0 ? MaxItemsCeiling : MaxItems, 1, MaxItemsCeiling),
        MaxDepth = Math.Clamp(MaxDepth <= 0 ? 2 : MaxDepth, 1, MaxDepthCeiling),
        Concurrency = Math.Clamp(Concurrency <= 0 ? 1 : Concurrency, 1, ConcurrencyCeiling),
        Delay = Delay < TimeSpan.Zero ? TimeSpan.Zero : Delay,
        Timeout = Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : Timeout,
        Retries = Math.Clamp(Retries <= 0 ? 1 : Retries, 1, 10),
        MaxResponseBytes = Math.Clamp(
            MaxResponseBytes <= 0 ? ResponseBytesCeiling : MaxResponseBytes, 4096, ResponseBytesCeiling),
        MaxLinksPerPage = Math.Clamp(MaxLinksPerPage <= 0 ? 500 : MaxLinksPerPage, 1, 20_000),
        MaxConsecutiveFailures = Math.Clamp(
            MaxConsecutiveFailures <= 0 ? 5 : MaxConsecutiveFailures, 1, 1_000)
    };
}
