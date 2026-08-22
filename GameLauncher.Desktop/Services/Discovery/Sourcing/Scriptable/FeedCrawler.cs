using GameLauncher.Desktop.Services.Discovery.Crawling;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// How a manifest crawls a site's own pages for its catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The third way a manifest can fill the catalogue, alongside a JSON or YAML
/// feed and an external script. It exists because most sites do not publish a
/// feed: they publish pages, and a page is a perfectly good catalogue if
/// something is willing to read it.
/// </para>
/// <para>
/// Pointing this at a <c>url</c> and nothing else is the intended starting
/// point. The crawler infers the rest, and every inference can be replaced by
/// one line of CSS in <see cref="Selectors"/> when it guesses wrong — which is
/// cheaper for the person writing the manifest than describing a whole page, and
/// cheaper for this code than a heuristic clever enough to need no help.
/// </para>
/// </remarks>
public sealed class FeedCrawler
{
    /// <summary>Whether this manifest crawls at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The listing page to start from.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Hosts the crawl may reach, or empty for the starting page's host alone.
    /// </summary>
    /// <remarks>
    /// Confined by default. A site whose pages link to a separate download or
    /// image host needs that host naming here; without the confinement, one page
    /// of outbound links would turn one configured source into a walk of the
    /// open web.
    /// </remarks>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// Whether this crawl may reach this machine and private networks.
    /// </summary>
    /// <remarks>
    /// For a repository genuinely hosted on the local network, and off by
    /// default everywhere else. Crawled HTML is untrusted input, and a link is
    /// the cheapest way to ask a program to fetch something behind the firewall
    /// it happens to be inside.
    /// </remarks>
    public bool AllowPrivateHosts { get; set; }

    /// <summary>How many listing pages to walk.</summary>
    public int MaxPages { get; set; } = 100;

    /// <summary>How many games to take in total.</summary>
    public int MaxItems { get; set; } = 5_000;

    /// <summary>How far from the starting page links may be followed.</summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>How many pages to read at once.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>Smallest gap between the start of two requests.</summary>
    public int DelayMilliseconds { get; set; } = 1_000;

    /// <summary>How long to wait for one response.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Attempts per page before giving up on it.</summary>
    public int Retries { get; set; } = 3;

    /// <summary>Largest page this crawl will read, in bytes.</summary>
    public int MaxPageBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Whether to read each game's own page as well as the listing.
    /// </summary>
    /// <remarks>
    /// On by default, because a listing page carries a title and a link and
    /// little else, and the description, artwork and credits that make a card
    /// worth looking at are on the detail page. Turning it off makes an import
    /// dramatically faster and dramatically thinner, which is the right trade
    /// for a first look at an unfamiliar site.
    /// </remarks>
    public bool ReadDetailPages { get; set; } = true;

    /// <summary>Selector overrides.</summary>
    public CrawlSelectors Selectors { get; set; } = new();

    /// <summary>
    /// Turns the manifest's numbers into engine limits.
    /// </summary>
    /// <returns>Limits, clamped to what the engine will honour.</returns>
    public CrawlLimits ToLimits() => new CrawlLimits
    {
        MaxPages = MaxPages,
        MaxItems = MaxItems,
        MaxDepth = MaxDepth,
        Concurrency = Concurrency,
        Delay = TimeSpan.FromMilliseconds(Math.Max(0, DelayMilliseconds)),
        Timeout = TimeSpan.FromSeconds(TimeoutSeconds <= 0 ? 30 : TimeoutSeconds),
        Retries = Retries,
        MaxResponseBytes = MaxPageBytes
    }.Normalized();

    /// <summary>
    /// Turns the manifest's host rules into an address policy.
    /// </summary>
    /// <returns>The policy this crawl runs under.</returns>
    public UrlPolicy ToPolicy() => new()
    {
        Schemes = ["http", "https"],
        AllowedHosts = AllowedHosts.Where(host => !string.IsNullOrWhiteSpace(host)).ToArray(),
        AllowPrivateAddresses = AllowPrivateHosts
    };

    /// <summary>
    /// Lists what is wrong with this section.
    /// </summary>
    /// <returns>Problems found, empty when it is usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Url))
        {
            problems.Add("'crawler.url' is required — a crawl needs a page to start from.");
        }
        else if (UrlGuard.Canonicalize(Url) is not { } address)
        {
            problems.Add($"'crawler.url' is not a usable address: '{Url}'.");
        }
        else if (address.Scheme is not ("http" or "https"))
        {
            problems.Add($"'crawler.url' must be http or https, not '{address.Scheme}'.");
        }

        return problems;
    }
}
