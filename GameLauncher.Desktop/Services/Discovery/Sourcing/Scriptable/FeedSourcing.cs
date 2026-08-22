namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// How a manifest works out where a game can be downloaded from.
/// </summary>
public enum SourcingStrategy
{
    /// <summary>
    /// Read the addresses off the game's own page.
    /// </summary>
    /// <remarks>
    /// The ordinary case for a site that publishes files: a release page with a
    /// link on it, and often a checksum and a size printed beside it.
    /// </remarks>
    DirectLink = 0,

    /// <summary>
    /// Take the address the catalogue already recorded.
    /// </summary>
    /// <remarks>
    /// For a source whose catalogue half already emitted a download address —
    /// a JSON feed, or a script that returned one. Nothing is fetched, because
    /// the answer is already in hand.
    /// </remarks>
    MappedField = 1,

    /// <summary>
    /// Ask an external program.
    /// </summary>
    /// <remarks>
    /// The escape hatch for a site whose pages cannot be read declaratively. The
    /// program is handed a description of the listing and returns candidates.
    /// </remarks>
    ExternalScript = 2
}

/// <summary>
/// When a manifest works out its download addresses.
/// </summary>
public enum SourcingResolution
{
    /// <summary>
    /// At install time, for the one game being installed.
    /// </summary>
    /// <remarks>
    /// The default, and almost always the right answer. A catalogue of several
    /// thousand games would otherwise cost several thousand extra page fetches
    /// per import to answer a question about the one game somebody eventually
    /// clicks — and the addresses would be stale by the time they did.
    /// </remarks>
    Lazy = 0,

    /// <summary>
    /// During the import, for every game.
    /// </summary>
    /// <remarks>
    /// Worth it only for a small catalogue where the download address is part of
    /// what makes a listing worth showing — a shelf of a few dozen files on a
    /// home server, where the extra fetches are cheap and knowing the size up
    /// front is useful.
    /// </remarks>
    Eager = 1
}

/// <summary>
/// Which part of a page carries each piece of a download.
/// </summary>
/// <remarks>
/// All optional but <see cref="DownloadLink"/>, and even that is inferred when
/// absent. A page that prints a checksum beside its download is publishing
/// something worth keeping, and mapping it here is what carries it into the
/// existing verification path instead of leaving the transfer unchecked.
/// </remarks>
public sealed class SourcingSelectors
{
    /// <summary>Links to the file itself.</summary>
    public string? DownloadLink { get; set; }

    /// <summary>A published checksum.</summary>
    public string? Checksum { get; set; }

    /// <summary>A published SHA-256, when the page distinguishes them.</summary>
    public string? Sha256 { get; set; }

    /// <summary>A published SHA-1.</summary>
    public string? Sha1 { get; set; }

    /// <summary>A published MD5.</summary>
    public string? Md5 { get; set; }

    /// <summary>A printed file size.</summary>
    public string? Size { get; set; }

    /// <summary>A printed file name.</summary>
    public string? FileName { get; set; }
}

/// <summary>
/// How a manifest resolves a listing to a download.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the catalogue half on purpose. A site can be worth indexing
/// without being a place anything is fetched from, and a site can supply
/// downloads for listings some other source found. Requiring both would make the
/// first case impossible to express.
/// </para>
/// <para>
/// Nothing here transfers a file. These sections decide <em>which address</em>,
/// and the existing download stack decides everything after that — which is why
/// a checksum found here is mapped onto the same fields every other source uses
/// rather than verified locally.
/// </para>
/// </remarks>
public sealed class FeedSourcing
{
    /// <summary>Whether this manifest resolves downloads at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How to work the addresses out.</summary>
    public SourcingStrategy Strategy { get; set; } = SourcingStrategy.DirectLink;

    /// <summary>When to work them out.</summary>
    public SourcingResolution Resolution { get; set; } = SourcingResolution.Lazy;

    /// <summary>
    /// Where this manifest's addresses sit in the merged mirror list.
    /// </summary>
    /// <remarks>
    /// Defaults to 100, above the zero every built-in adapter sits at. Someone
    /// who wrote a manifest for a host the launcher already handles meant it to
    /// be used; lower it to place the feed behind the built-ins, and go negative
    /// to make it a last resort.
    /// </remarks>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Hosts the resolved addresses may point at, or empty for the page's own.
    /// </summary>
    /// <remarks>
    /// A release page linking to a separate file host is the normal case, so
    /// this is how that host is permitted. Left empty, an address on another
    /// host is refused — which is the safe direction when the link came out of
    /// a page written by somebody else.
    /// </remarks>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Whether resolved addresses may point at private networks.</summary>
    public bool AllowPrivateHosts { get; set; }

    /// <summary>Whether a magnet address is acceptable.</summary>
    /// <remarks>
    /// Off by default. A magnet needs an external engine that may not be
    /// installed, so accepting one where an HTTP address was expected turns a
    /// working install into a mysterious failure.
    /// </remarks>
    public bool AllowMagnet { get; set; }

    /// <summary>Which part of a page carries each piece of a download.</summary>
    public SourcingSelectors Selectors { get; set; } = new();

    /// <summary>The program to run, for <see cref="SourcingStrategy.ExternalScript"/>.</summary>
    public FeedTransform? Script { get; set; }

    /// <summary>
    /// Lists what is wrong with this section.
    /// </summary>
    /// <returns>Problems found, empty when it is usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Strategy == SourcingStrategy.ExternalScript)
        {
            if (Script is null)
            {
                problems.Add("'sourcing.script' is required when the strategy is external-script.");
            }
            else if (string.IsNullOrWhiteSpace(Script.Command))
            {
                problems.Add("'sourcing.script.command' is required.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Turns the manifest's host rules into an address policy.
    /// </summary>
    /// <param name="pageHost">The host of the page being resolved.</param>
    /// <returns>The policy resolved addresses must satisfy.</returns>
    public Crawling.UrlPolicy ToPolicy(string? pageHost)
    {
        var hosts = AllowedHosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim())
            .ToList();

        // The page's own host is always acceptable: a file beside the page that
        // advertised it is the least surprising thing a link can point at.
        if (!string.IsNullOrWhiteSpace(pageHost) && !hosts.Contains(pageHost, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(pageHost);
        }

        return new Crawling.UrlPolicy
        {
            Schemes = AllowMagnet ? ["http", "https", "magnet"] : ["http", "https"],
            AllowedHosts = hosts,
            AllowPrivateAddresses = AllowPrivateHosts
        };
    }
}
