namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// The shape a feed's payload arrives in.
/// </summary>
public enum FeedFormat
{
    /// <summary>A JSON document.</summary>
    Json = 0,

    /// <summary>A YAML document.</summary>
    Yaml = 1,

    /// <summary>An RSS 2.0 or Atom feed.</summary>
    /// <remarks>
    /// One value rather than two. The reader normalises both into the same node
    /// tree, so a manifest describes where its fields are, not which of the two
    /// dialects the publisher chose.
    /// </remarks>
    Feed = 2
}

/// <summary>
/// Where a manifest gets its payload from.
/// </summary>
public sealed class FeedRequest
{
    /// <summary>
    /// Address template, or a path relative to the adapter directory.
    /// </summary>
    /// <remarks>
    /// Placeholders in braces are substituted from the listing being installed:
    /// <c>{url}</c>, <c>{title}</c>, <c>{year}</c>, <c>{id}</c> and
    /// <c>{sourceItemId}</c>. A value with no scheme is read from a file beside
    /// the manifest, which is how a purely local catalogue works with no server
    /// at all.
    /// </remarks>
    public string Url { get; set; } = string.Empty;

    /// <summary>Extra request headers, if the feed needs any.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
}

/// <summary>
/// Which addresses a manifest claims.
/// </summary>
public sealed class FeedMatch
{
    /// <summary>
    /// Hosts this manifest handles, matched on suffix.
    /// </summary>
    /// <remarks>
    /// A suffix match, so <c>example.org</c> also claims
    /// <c>files.example.org</c>. Listed explicitly rather than inferred from the
    /// request address, because the page a listing points at and the endpoint
    /// that describes its files are frequently different hosts.
    /// </remarks>
    public List<string> Hosts { get; set; } = [];

    /// <summary>
    /// Substrings the address must contain, if any.
    /// </summary>
    /// <remarks>
    /// Narrows a manifest to part of a site — <c>/games/</c> on a host that also
    /// serves other things. Empty means the host alone decides.
    /// </remarks>
    public List<string> PathContains { get; set; } = [];
}

/// <summary>
/// Which field of a payload item supplies each part of a download.
/// </summary>
/// <remarks>
/// Every value is a path into the item, in the same dotted form throughout:
/// <c>files.0.name</c> walks objects and arrays alike, and <c>@href</c> reads an
/// XML attribute. Only <see cref="Url"/> is required — a feed that publishes
/// nothing but addresses is still a usable feed.
/// </remarks>
public sealed class FeedDownloadMap
{
    /// <summary>Path to the download address. Required.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Path to the file name.</summary>
    public string? FileName { get; set; }

    /// <summary>Path to the size in bytes.</summary>
    public string? SizeBytes { get; set; }

    /// <summary>Path to a SHA-256 digest in hex.</summary>
    /// <remarks>
    /// Preferred over the other two when the feed offers a choice. A feed that
    /// publishes its own files can usually say this; one mirroring someone
    /// else's reports whatever that someone published, which is why all three
    /// are mappable rather than one.
    /// </remarks>
    public string? Sha256 { get; set; }

    /// <summary>Path to an SHA-1 digest in hex.</summary>
    public string? Sha1 { get; set; }

    /// <summary>Path to an MD5 digest in hex.</summary>
    public string? Md5 { get; set; }

    /// <summary>Path to a format label, such as <c>ZIP</c>.</summary>
    public string? Format { get; set; }

    /// <summary>Path to a title, used when the feed also describes the game.</summary>
    public string? Title { get; set; }
}

/// <summary>
/// An external program that turns a payload into something this launcher maps.
/// </summary>
/// <remarks>
/// <para>
/// This is what "script hook" means here: a process the user nominates, handed
/// the fetched payload on standard input and expected to write JSON to standard
/// output. Lua, JavaScript, Python or a compiled binary all work, because the
/// contract is a pipe rather than an embedded interpreter.
/// </para>
/// <para>
/// Nothing is bundled to run these. The launcher does not host a scripting
/// engine, so a hook only ever runs a program the user already has and has named
/// in a file they wrote.
/// </para>
/// </remarks>
public sealed class FeedTransform
{
    /// <summary>Program to run.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments passed to it.</summary>
    /// <remarks>
    /// A list rather than one string, so an argument containing a space is one
    /// argument. Building a command line by concatenation is how a file path
    /// with a space in it becomes two broken arguments.
    /// </remarks>
    public List<string> Args { get; set; } = [];

    /// <summary>How long to wait before giving up on it.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Which field of a catalogue item supplies each part of a listing.
/// </summary>
/// <remarks>
/// Only <see cref="Title"/> is required. A feed that lists nothing but names is
/// still a catalogue — the launcher can find the game, and one of the sourcing
/// adapters can work out how to fetch it.
/// </remarks>
public sealed class FeedCatalogMap
{
    /// <summary>Path to the item's title. Required.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Path to the source's own identifier for the item.</summary>
    /// <remarks>
    /// Defaults to the title when absent. It only has to be stable and unique
    /// within this feed: it is how a re-import recognises an item it has already
    /// seen rather than adding it twice.
    /// </remarks>
    public string? Id { get; set; }

    /// <summary>Path to the release year, as a number.</summary>
    public string? Year { get; set; }

    /// <summary>
    /// Path to a publication timestamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the common case of a feed that dates its entries rather than
    /// numbering their year — <c>2026-01-17T16:44:40Z</c> and
    /// <c>2025-10-04 16:52:20</c> are both read. Mapping <see cref="Year"/> at
    /// a field like that yields nothing, because a year is parsed as a number
    /// and a timestamp is not one.
    /// </para>
    /// <para>
    /// Worth mapping for two separate reasons. It supplies the year when the
    /// feed has no plain one, which the matcher uses to tell a remake from an
    /// original; and it becomes the observation's change stamp, which is how an
    /// incremental import knows an entry has been edited since it was last
    /// read. Without it every pass re-reads the whole feed.
    /// </para>
    /// </remarks>
    public string? PubDate { get; set; }

    /// <summary>Path to a description.</summary>
    public string? Description { get; set; }

    /// <summary>Path to the developer.</summary>
    public string? Developer { get; set; }

    /// <summary>Path to the publisher.</summary>
    public string? Publisher { get; set; }

    /// <summary>Path to a cover image address.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Path to the item's own page, where the feed publishes one.</summary>
    public string? Page { get; set; }

    /// <summary>Path to a direct download address, where the feed has one.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Path to the download's size in bytes.</summary>
    public string? SizeBytes { get; set; }

    /// <summary>Path to a SHA-256 digest in hex.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Path to a SHA-1 digest in hex.</summary>
    public string? Sha1 { get; set; }

    /// <summary>Path to an MD5 digest in hex.</summary>
    public string? Md5 { get; set; }

    /// <summary>Path to the download's file name.</summary>
    public string? FileName { get; set; }
}

/// <summary>
/// How a manifest fills the catalogue, as opposed to how it supplies downloads.
/// </summary>
/// <remarks>
/// <para>
/// The two halves answer different questions and a manifest may do either or
/// both. <c>match</c> and <c>map</c> say "given this listing, what can be
/// downloaded"; this says "what games exist". A manifest with only the first is
/// inert until something else has already put listings in the catalogue, which
/// is the single most confusing thing about writing one.
/// </para>
/// <para>
/// Deliberately its own section rather than reusing the sourcing request. That
/// one is per-listing and substitutes <c>{title}</c> into a lookup; this one is
/// fetched once and returns many items. Sharing the fields would have meant one
/// URL trying to be both.
/// </para>
/// </remarks>
public sealed class FeedCatalog
{
    /// <summary>Whether this manifest contributes to the catalogue at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where the list of games comes from.</summary>
    public FeedRequest Request { get; set; } = new();

    /// <summary>The payload's shape.</summary>
    public FeedFormat Format { get; set; } = FeedFormat.Json;

    /// <summary>Path to the list of items within the payload.</summary>
    public string Items { get; set; } = string.Empty;

    /// <summary>Which field of an item supplies each part of a listing.</summary>
    public FeedCatalogMap Map { get; set; } = new();

    /// <summary>An optional external program run over the payload first.</summary>
    public FeedTransform? Transform { get; set; }

    /// <summary>
    /// Address template for an item's page, with <c>{id}</c> substituted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the common case of a feed that publishes identifiers rather than
    /// addresses. <c>https://archive.org/details/{id}</c> turns one into the
    /// other, and the mapping language cannot: it walks a payload, it does not
    /// build strings.
    /// </para>
    /// <para>
    /// Worth getting right, because this address is what the sourcing adapters
    /// dispatch on. A feed that only lists names can still produce installable
    /// listings, as long as the page it points at is one some adapter handles.
    /// </para>
    /// </remarks>
    public string? PageTemplate { get; set; }

    /// <summary>
    /// Lists what is wrong with this section.
    /// </summary>
    /// <returns>Problems found, empty when it is usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Request.Url))
        {
            problems.Add("'catalog.request.url' is required.");
        }

        if (string.IsNullOrWhiteSpace(Map.Title))
        {
            problems.Add("'catalog.map.title' is required — a listing with no name is not a listing.");
        }

        if (Transform is { } transform && string.IsNullOrWhiteSpace(transform.Command))
        {
            problems.Add("'catalog.transform.command' is required when a transform is declared.");
        }

        return problems;
    }
}

/// <summary>
/// One user-supplied sourcing feed, read from the adapter directory.
/// </summary>
/// <remarks>
/// <para>
/// The unit of extension. A file describing where to fetch, how to read what
/// comes back, and which field is which, is enough to add a source without
/// touching this codebase — which is the point, because the interesting feeds
/// are the ones nobody here has heard of.
/// </para>
/// <para>
/// Deliberately declarative. The stated job is mapping a payload onto download
/// addresses, and mapping is data; a manifest that needed code to express it
/// would be a program, and this launcher is not the right place to host one.
/// <see cref="Transform"/> exists for the cases that genuinely do need code, and
/// hands them to a process the user chose.
/// </para>
/// </remarks>
public sealed class FeedManifest
{
    /// <summary>Dispatch key, unique across manifests.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Name shown to a person.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether this manifest is used at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where this feed's addresses land in the merged mirror list; higher first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to 100, which is above the zero every built-in adapter sits at.
    /// Someone who wrote a manifest for a host this launcher already handles
    /// meant it to be used, and having to say so twice — once by writing the
    /// file and again by numbering it — would be a poor default.
    /// </para>
    /// <para>
    /// Lower it to place a feed behind the built-ins. A negative value is
    /// perfectly reasonable and means "only if nothing else worked", which is
    /// what a slow mirror or a flaky home server is worth.
    /// </para>
    /// </remarks>
    public int Priority { get; set; } = 100;

    /// <summary>Which addresses it claims.</summary>
    public FeedMatch Match { get; set; } = new();

    /// <summary>Where its payload comes from.</summary>
    public FeedRequest Request { get; set; } = new();

    /// <summary>The payload's shape.</summary>
    public FeedFormat Format { get; set; } = FeedFormat.Json;

    /// <summary>
    /// Path to the list of downloadable items within the payload.
    /// </summary>
    /// <remarks>
    /// Empty means the payload is itself the list. A path reaching a single node
    /// rather than a list is treated as a list of one, so a feed describing one
    /// file needs no special case.
    /// </remarks>
    public string Items { get; set; } = string.Empty;

    /// <summary>Which field of an item supplies each part of a download.</summary>
    public FeedDownloadMap Map { get; set; } = new();

    /// <summary>An optional external program run over the payload first.</summary>
    public FeedTransform? Transform { get; set; }

    /// <summary>
    /// How this manifest fills the catalogue, when it does.
    /// </summary>
    /// <remarks>
    /// Absent on a manifest that only supplies downloads for listings some other
    /// source found. Present, it makes the same file a catalogue source in its
    /// own right, which is what stops a hand-written feed being inert until
    /// something else has populated the catalogue first.
    /// </remarks>
    public FeedCatalog? Catalog { get; set; }

    /// <summary>
    /// How this manifest crawls a site's pages, when it does.
    /// </summary>
    /// <remarks>
    /// The alternative to <see cref="Catalog"/> for a site that publishes pages
    /// rather than a feed, which is most of them. The two are mutually
    /// exclusive in practice — a manifest declaring both would be describing the
    /// same catalogue twice — and the crawler is preferred when both appear.
    /// </remarks>
    public FeedCrawler? Crawler { get; set; }

    /// <summary>
    /// How this manifest resolves a listing to a download, when it does.
    /// </summary>
    public FeedSourcing? Sourcing { get; set; }

    /// <summary>Gets a value indicating whether this manifest is a catalogue feed.</summary>
    /// <remarks>
    /// Strictly the feed half. A crawler fills the catalogue too, but through a
    /// different source that fetches pages rather than a payload — and this
    /// property is what the feed reader iterates, so widening it to mean "fills
    /// the catalogue somehow" hands it manifests with no <c>catalog</c> section
    /// to read. Ask <see cref="ProvidesCrawler"/> for the other half.
    /// </remarks>
    public bool ProvidesCatalog => Catalog is { Enabled: true };

    /// <summary>Gets a value indicating whether this manifest crawls a site.</summary>
    public bool ProvidesCrawler => Crawler is { Enabled: true } && !string.IsNullOrWhiteSpace(Crawler.Url);

    /// <summary>Gets a value indicating whether this manifest resolves downloads.</summary>
    public bool ProvidesSourcing => Sourcing is { Enabled: true };

    /// <summary>
    /// Gets the priority this manifest's addresses rank at.
    /// </summary>
    /// <remarks>
    /// The <c>sourcing</c> section's own number when it has one, so a manifest
    /// can rank its downloads separately from the legacy top-level
    /// <see cref="Priority"/> that the feed-mapping path uses.
    /// </remarks>
    public int SourcingPriority => Sourcing?.Priority ?? Priority;

    /// <summary>File this manifest was read from, for diagnostics.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Lists what is wrong with this manifest.
    /// </summary>
    /// <returns>Problems found, empty when it is usable.</returns>
    /// <remarks>
    /// Returns every problem rather than the first, because a person editing a
    /// file by hand would otherwise fix one, run again, and be told about the
    /// next.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Key))
        {
            problems.Add("'key' is required.");
        }

        // The legacy feed-mapping path, recognised by the fields only it uses.
        // 'match' is deliberately not one of them: the newer 'sourcing' section
        // needs it too, to say which hosts it claims, and treating its presence
        // as intent to use the old path made a perfectly good manifest fail with
        // a complaint about a 'map.url' it had no reason to have.
        var usesFeedMapping =
            !string.IsNullOrWhiteSpace(Request.Url) ||
            !string.IsNullOrWhiteSpace(Map.Url);

        if (usesFeedMapping)
        {
            if (Match.Hosts.Count == 0)
            {
                problems.Add("'match.hosts' must name at least one host.");
            }

            if (string.IsNullOrWhiteSpace(Request.Url))
            {
                problems.Add("'request.url' is required.");
            }

            if (string.IsNullOrWhiteSpace(Map.Url))
            {
                problems.Add("'map.url' is required — a feed with no address supplies nothing.");
            }
        }
        else if (Match.Hosts.Count > 0 && Catalog is null && Crawler is null && Sourcing is null)
        {
            // 'match' on its own claims addresses and then does nothing with
            // them, which is a file that looks like it works and does not.
            problems.Add(
                "'match' names hosts but nothing resolves them: add a 'sourcing' section, " +
                "or a 'request'/'map' pair for the older feed-mapping path.");
        }
        else if (Match.Hosts.Count == 0 && Catalog is null && Crawler is null && Sourcing is null)
        {
            problems.Add(
                "a manifest must do something: give it a 'crawler' or 'catalog' section, " +
                "a 'sourcing' section, a legacy 'match'/'request'/'map', or a combination.");
        }

        if (Transform is { } transform && string.IsNullOrWhiteSpace(transform.Command))
        {
            problems.Add("'transform.command' is required when a transform is declared.");
        }

        if (Catalog is { } catalog)
        {
            problems.AddRange(catalog.Validate());
        }

        if (Crawler is { } crawler)
        {
            problems.AddRange(crawler.Validate());
        }

        if (Sourcing is { } sourcing)
        {
            problems.AddRange(sourcing.Validate());
        }

        return problems;
    }
}
