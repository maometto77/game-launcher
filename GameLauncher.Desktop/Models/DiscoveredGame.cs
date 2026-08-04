namespace GameLauncher.Desktop.Models;

/// <summary>
/// An executable found while scanning a folder, and what the launcher makes of it.
/// </summary>
/// <remarks>
/// A candidate, never a decision. Nothing here is added to the library until the
/// user explicitly selects it; <see cref="IsLikelyGame"/> only controls whether
/// the row starts out ticked.
/// </remarks>
public sealed class DiscoveredGame
{
    /// <summary>Metadata read from the executable.</summary>
    public required ExecutableInfo Executable { get; init; }

    /// <summary>Best guess at the folder the game is installed in.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>
    /// Whether this looks like a game rather than a support tool.
    /// </summary>
    /// <remarks>
    /// Drives the default tick state only. Everything found is still listed, because
    /// a heuristic that silently hides the one executable the user wanted is worse
    /// than one that shows a few they do not.
    /// </remarks>
    public bool IsLikelyGame { get; init; }

    /// <summary>Whether an entry with this executable path already exists in the library.</summary>
    public bool IsAlreadyInLibrary { get; init; }

    /// <summary>
    /// A short explanation of why this candidate was ranked as it was, shown
    /// beside the row.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>Gets the suggested display title.</summary>
    public string SuggestedTitle => Executable.SuggestedTitle;

    /// <summary>Gets the absolute path to the executable.</summary>
    public string ExecutablePath => Executable.Path;
}

/// <summary>
/// Progress reported while a folder scan runs.
/// </summary>
/// <param name="DirectoriesScanned">How many directories have been visited.</param>
/// <param name="CandidatesFound">How many executables have been accepted as candidates.</param>
/// <param name="CurrentDirectory">The directory being examined, for display.</param>
public sealed record ScanProgress(int DirectoriesScanned, int CandidatesFound, string CurrentDirectory);
