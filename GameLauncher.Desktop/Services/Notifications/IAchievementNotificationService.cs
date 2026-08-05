using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Notifications;

/// <summary>
/// One achievement waiting to be, or currently being, announced.
/// </summary>
/// <param name="Definition">The achievement that was earned.</param>
/// <param name="Game">The game it belongs to, or <see langword="null"/> for a library-wide one.</param>
/// <param name="UnlockedAt">When it was earned.</param>
public sealed record AchievementNotification(
    AchievementDefinition Definition,
    Game? Game,
    DateTimeOffset UnlockedAt);

/// <summary>
/// Describes a change to what is currently being announced.
/// </summary>
/// <param name="Current">
/// The notification now on screen, or <see langword="null"/> when nothing is.
/// </param>
/// <param name="PendingCount">
/// How many were queued behind it at the moment it appeared. Not refreshed while
/// it is on screen — an unlock arriving mid-announcement is counted from the next
/// one, which keeps the event meaning "what is showing changed" rather than
/// firing repeatedly for the same announcement.
/// </param>
public sealed record AchievementNotificationChangedEventArgs(
    AchievementNotification? Current,
    int PendingCount);

/// <summary>
/// Announces earned achievements one at a time, in the order they were earned.
/// </summary>
/// <remarks>
/// <para>
/// Sits between the engine and the interface. It subscribes to
/// <see cref="Achievements.IAchievementEngine.AchievementUnlocked"/> and nothing
/// else; the engine has no idea it exists, and no toast can be produced by any
/// route other than a genuine unlock.
/// </para>
/// <para>
/// Queueing lives here rather than in a view model because ordering and dwell
/// timing are application logic: several achievements can be earned in a single
/// evaluation pass, and showing them on top of one another would lose all but the
/// last. A view model's job is to render whatever is current.
/// </para>
/// </remarks>
public interface IAchievementNotificationService
{
    /// <summary>
    /// Raised when the announcement on screen changes, including when the last
    /// one is dismissed.
    /// </summary>
    /// <remarks>
    /// Raised from the pump's own thread. Subscribers that touch the interface
    /// must marshal it themselves; the service has no view to be bound to and so
    /// makes no assumption about which thread its consumer needs.
    /// </remarks>
    event EventHandler<AchievementNotificationChangedEventArgs>? CurrentChanged;

    /// <summary>Gets the announcement currently on screen, if any.</summary>
    AchievementNotification? Current { get; }

    /// <summary>
    /// Gets how many announcements were queued behind <see cref="Current"/> when
    /// it appeared.
    /// </summary>
    int PendingCount { get; }

    /// <summary>
    /// Ends the current announcement immediately and moves to the next.
    /// </summary>
    /// <remarks>
    /// Dismissing skips only the announcement, never the unlock: the achievement
    /// is already recorded by the time this service hears about it.
    /// </remarks>
    void DismissCurrent();
}
