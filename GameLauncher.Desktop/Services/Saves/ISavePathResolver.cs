namespace GameLauncher.Desktop.Services.Saves;

/// <summary>
/// Where a game keeps something worth preserving.
/// </summary>
/// <param name="Path">
/// An absolute path on this machine, with every placeholder already resolved.
/// </param>
/// <param name="Kind">Whether it is a file, a directory or a registry key.</param>
/// <param name="Tags">What the manifest says it holds — <c>save</c>, <c>config</c>.</param>
/// <param name="Exists">Whether it is actually present right now.</param>
public sealed record SaveLocation(
    string Path,
    SaveLocationKind Kind,
    IReadOnlyList<string> Tags,
    bool Exists);

/// <summary>
/// What a resolved save location refers to.
/// </summary>
public enum SaveLocationKind
{
    /// <summary>A file, or a glob matching files.</summary>
    File = 0,

    /// <summary>A directory holding saves.</summary>
    Directory = 1,

    /// <summary>
    /// A Windows registry key.
    /// </summary>
    /// <remarks>
    /// Reported so a caller knows it exists, not acted upon here. Plenty of
    /// older games keep progress in the registry, and a save feature that
    /// silently ignored them would lose exactly the games this launcher is for.
    /// </remarks>
    Registry = 2
}

/// <summary>
/// What was asked about.
/// </summary>
public sealed record SavePathQuery
{
    /// <summary>The game's title, as the library knows it.</summary>
    public required string Title { get; init; }

    /// <summary>Steam application id, when known.</summary>
    /// <remarks>
    /// Preferred over the title when present: it identifies a game exactly,
    /// whereas titles have to be matched and can be wrong.
    /// </remarks>
    public int? SteamAppId { get; init; }

    /// <summary>
    /// Where the game is installed, used to resolve <c>&lt;base&gt;</c> and
    /// <c>&lt;root&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Without it, any manifest entry relative to the installation is
    /// unresolvable and is skipped rather than guessed at.
    /// </remarks>
    public string? InstallDirectory { get; init; }

    /// <summary>Whether to return locations that do not exist on this machine.</summary>
    public bool IncludeMissing { get; init; }
}

/// <summary>
/// What was found.
/// </summary>
/// <param name="MatchedTitle">The manifest entry that matched, or <see langword="null"/>.</param>
/// <param name="Locations">Resolved locations, save-tagged first.</param>
public sealed record SavePathResult(string? MatchedTitle, IReadOnlyList<SaveLocation> Locations)
{
    /// <summary>An empty result, for a game the manifest does not cover.</summary>
    public static SavePathResult NotFound { get; } = new(null, []);

    /// <summary>Gets a value indicating whether the manifest knew this game.</summary>
    public bool Found => MatchedTitle is not null;
}

/// <summary>
/// Works out where a game keeps its saves.
/// </summary>
/// <remarks>
/// <para>
/// Backed by the Ludusavi community manifest, which is a maintained catalogue of
/// save locations for thousands of PC games. Treating it as a data dependency
/// rather than hardcoding paths is the whole point: the knowledge is large,
/// changes constantly, and is already being curated by people who care about it.
/// </para>
/// <para>
/// The manifest is fetched, cached on disk and refreshed occasionally. A game it
/// does not cover is an ordinary outcome, not an error — the answer is simply
/// that nothing is known, and a caller falls back to asking the user.
/// </para>
/// </remarks>
public interface ISavePathResolver
{
    /// <summary>
    /// Gets a value indicating whether a usable manifest is available.
    /// </summary>
    /// <param name="cancellationToken">Cancels any fetch this triggers.</param>
    /// <returns><see langword="true"/> when lookups can be answered.</returns>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds where a game keeps its saves.
    /// </summary>
    /// <param name="query">What to look up.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>What the manifest knows, or <see cref="SavePathResult.NotFound"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    Task<SavePathResult> ResolveAsync(SavePathQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the manifest again, whether or not the cached copy is stale.
    /// </summary>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>How many games the refreshed manifest covers, or zero on failure.</returns>
    Task<int> RefreshAsync(CancellationToken cancellationToken = default);
}
