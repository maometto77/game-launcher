using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Achievements.Emulators;

/// <summary>
/// One achievement file to read, and what to record it as.
/// </summary>
/// <param name="SourceKey">Stable key for the writer that produced it.</param>
/// <param name="DisplayName">What to call that writer when telling a person.</param>
/// <param name="Root">Directory holding one folder per application.</param>
/// <remarks>
/// A root rather than a file: every one of these writers keeps
/// <c>&lt;root&gt;/&lt;appid&gt;/achievements.*</c>, so the application id is the
/// folder name and one watcher covers every game at once.
/// </remarks>
public sealed record AchievementSourceRoot(string SourceKey, string DisplayName, string Root);

/// <summary>
/// Something newly earned, as read off the disk.
/// </summary>
/// <param name="Achievement">The achievement that changed state.</param>
/// <param name="Game">The library entry it belongs to, or <see langword="null"/>.</param>
/// <param name="DisplayName">What to call the writer that reported it.</param>
public sealed record ExternalAchievementUnlockedEventArgs(
    ExternalAchievement Achievement,
    Game? Game,
    string DisplayName);

/// <summary>
/// Watches the folders Steam emulators write achievements into.
/// </summary>
/// <remarks>
/// <para>
/// A different approach from the memory provider, and a complementary one. The
/// memory provider needs the game running and an address that survives a patch;
/// this needs neither. Every one of these writers records unlocks to a file the
/// moment they happen, so watching the file is both cheaper and more reliable —
/// and it still works for a session the launcher was not running for, because
/// the file is read at startup too.
/// </para>
/// <para>
/// Nothing here is written to the curated achievement catalogue. These are
/// observations of another program's records, kept in their own table, and
/// surfaced as their own section. Letting a file on disk mint catalogue rows
/// would put unauthored content into the relay's sync path.
/// </para>
/// </remarks>
public interface IAchievementWatcherService
{
    /// <summary>
    /// Raised when an achievement that was locked is now unlocked.
    /// </summary>
    /// <remarks>
    /// Raised from a background thread. Subscribers touching the interface must
    /// marshal for themselves, in keeping with every other event in this
    /// application.
    /// </remarks>
    event EventHandler<ExternalAchievementUnlockedEventArgs>? AchievementUnlocked;

    /// <summary>Gets the roots currently being watched.</summary>
    IReadOnlyList<AchievementSourceRoot> WatchedRoots { get; }

    /// <summary>
    /// Reads every watched file now, without waiting for one to change.
    /// </summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>How many achievements were newly unlocked by this pass.</returns>
    /// <remarks>
    /// Run once at startup so a session played with the launcher closed is picked
    /// up, and available to the interface as a refresh.
    /// </remarks>
    Task<int> ScanAllAsync(CancellationToken cancellationToken = default);
}
