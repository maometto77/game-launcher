namespace GameLauncher.Desktop.Models;

/// <summary>
/// The top-level sections reachable from the sidebar.
/// </summary>
/// <remarks>
/// Used to drive sidebar selection independently of the concrete view model on
/// screen. A game details page is reached from Library and should keep Library
/// highlighted, which a "selected view model type" approach could not express.
/// </remarks>
public enum NavigationSection
{
    /// <summary>Landing page: recently played, resume, and library highlights.</summary>
    Home = 0,

    /// <summary>The full game library.</summary>
    Library = 1,

    /// <summary>Friends list, presence, and friend requests.</summary>
    Friends = 2,

    /// <summary>Collection management.</summary>
    Collections = 3,

    /// <summary>Library-wide achievement overview.</summary>
    Achievements = 4,

    /// <summary>Search across the library.</summary>
    Search = 5,

    /// <summary>Application settings.</summary>
    Settings = 6
}
