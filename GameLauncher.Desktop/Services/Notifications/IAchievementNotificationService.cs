using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Notifications;

/// <summary>
/// One achievement waiting to be, or currently being, announced.
/// </summary>
/// <param name="Title">The achievement's name.</param>
/// <param name="Description">What it was earned for.</param>
/// <param name="IconPath">Its icon, or <see langword="null"/> for the placeholder.</param>
/// <param name="Game">The game it belongs to, or <see langword="null"/> for a library-wide one.</param>
/// <param name="UnlockedAt">When it was earned.</param>
/// <remarks>
/// Carries what a toast needs rather than the row it came from. Achievements
/// now arrive by two routes — the catalogue's own providers, and a file some
/// emulator wrote — and a notification that named an
/// <see cref="AchievementDefinition"/> could only ever describe the first.
/// </remarks>
public sealed record AchievementNotification(
    string Title,
    string Description,
    string? IconPath,
    Game? Game,
    DateTimeOffset UnlockedAt)
{
    /// <summary>Builds a notification for a catalogue achievement.</summary>
    /// <param name="definition">The achievement that was earned.</param>
    /// <param name="game">The game it belongs to.</param>
    /// <param name="unlockedAt">When it was earned.</param>
    /// <returns>The notification.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static AchievementNotification FromDefinition(
        AchievementDefinition definition,
        Game? game,
        DateTimeOffset unlockedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new AchievementNotification(
            definition.Title, definition.Description, definition.IconPath, game, unlockedAt);
    }

    /// <summary>Builds a notification for an achievement read off the disk.</summary>
    /// <param name="achievement">The achievement that was earned.</param>
    /// <param name="game">The game it belongs to, if the library has it.</param>
    /// <param name="sourceName">What to call the writer that recorded it.</param>
    /// <returns>The notification.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="achievement"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The API name stands in for a title. These files record identifiers, not
    /// display names, and inventing a prettier one would mean guessing at what a
    /// game calls its own achievement.
    /// </remarks>
    public static AchievementNotification FromExternal(
        ExternalAchievement achievement,
        Game? game,
        string sourceName)
    {
        ArgumentNullException.ThrowIfNull(achievement);

        return new AchievementNotification(
            achievement.ApiName,
            $"Recorded by {sourceName}",
            null,
            game,
            achievement.UnlockedAt ?? DateTimeOffset.Now);
    }
}

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
