namespace GameLauncher.Desktop.Models;

/// <summary>
/// Which achievements the achievements page shows.
/// </summary>
/// <remarks>
/// A presentation concern only. Filtering happens over already-loaded rows, so
/// changing it never re-reads the database and never evaluates anything.
/// </remarks>
public enum AchievementFilter
{
    /// <summary>Everything, earned or not.</summary>
    All = 0,

    /// <summary>Only achievements that have been earned.</summary>
    Unlocked = 1,

    /// <summary>Only achievements still to earn.</summary>
    Locked = 2
}
