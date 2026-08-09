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

        if (Transform is { } transform && string.IsNullOrWhiteSpace(transform.Command))
        {
            problems.Add("'transform.command' is required when a transform is declared.");
        }

        return problems;
    }
}
