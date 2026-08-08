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

    /// <summary>
    /// The discovery catalogue: games that exist, as opposed to games installed.
    /// </summary>
    /// <remarks>
    /// A section of its own rather than a mode of the library. The library
    /// answers "what do I have"; this answers "what is out there". Conflating
    /// them would make every filter and every count ambiguous.
    /// </remarks>
    Discover = 5,

    /// <summary>
    /// The download queue.
    /// </summary>
    /// <remarks>
    /// A section rather than a panel on the Discover page: downloads outlive the
    /// page that started them, and burying a running transfer inside a catalogue
    /// browser makes it hard to find precisely when someone wants to check on it.
    /// </remarks>
    Downloads = 7,

    /// <summary>Application settings.</summary>
    /// <remarks>
    /// Numbered 6 rather than 5 because a <c>Search</c> member briefly sat
    /// between them and was removed — searching happens within a page rather than
    /// in a section of its own. The gap it left has since been taken by
    /// <see cref="Discover"/>. Settings keeps its value regardless: renumbering
    /// an enum gains nothing and would silently change the meaning of any value
    /// written down elsewhere.
    /// </remarks>
    Settings = 6
}
