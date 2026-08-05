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

    /// <summary>Application settings.</summary>
    /// <remarks>
    /// Numbered 6 rather than 5 because a <c>Search</c> member briefly sat
    /// between them. Searching happens within the library page — over titles and
    /// tags — rather than in a section of its own, so the member was removed once
    /// it was clear nothing would map to it. The gap is left as it is because
    /// renumbering an enum gains nothing and would silently change the meaning of
    /// any value written down elsewhere.
    /// </remarks>
    Settings = 6
}
