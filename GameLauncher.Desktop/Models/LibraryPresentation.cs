namespace GameLauncher.Desktop.Models;

/// <summary>
/// How the library presents its games.
/// </summary>
public enum LibraryViewMode
{
    /// <summary>Cover-art tiles in a wrapping grid.</summary>
    Grid = 0,

    /// <summary>Compact rows showing more metadata per game.</summary>
    List = 1
}

/// <summary>
/// The ordering applied to the library.
/// </summary>
public enum LibrarySortOrder
{
    /// <summary>Alphabetical by title.</summary>
    Title = 0,

    /// <summary>Most recently played first; never-played games sort last.</summary>
    LastPlayed = 1,

    /// <summary>Most played first, by accumulated playtime.</summary>
    Playtime = 2,

    /// <summary>Most recently added to the library first.</summary>
    DateAdded = 3,

    /// <summary>Largest install first.</summary>
    InstallSize = 4
}
