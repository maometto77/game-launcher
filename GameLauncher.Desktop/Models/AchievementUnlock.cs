namespace GameLauncher.Desktop.Models;

/// <summary>
/// Records that an achievement definition has been earned.
/// </summary>
/// <remarks>
/// The presence of a row is the unlock: there is no "locked" row and no boolean
/// flag. Unlocks are insert-only and are never revoked, so a rule that is later
/// edited or a save file that is later rolled back cannot take an achievement
/// away from the user.
/// </remarks>
public sealed class AchievementUnlock
{
    /// <summary>Identifier of the <see cref="AchievementDefinition"/> that was earned.</summary>
    public int DefinitionId { get; set; }

    /// <summary>When the achievement was unlocked.</summary>
    public DateTimeOffset UnlockedAt { get; set; }

    /// <summary>
    /// When this unlock was last pushed to a relay, or <see langword="null"/> if
    /// it never has been.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="UnlockedAt"/> so that re-synchronising can
    /// never rewrite when the achievement was actually earned. A null value is
    /// the queue: "everything not yet sent" is a single indexed predicate rather
    /// than a diff against the server.
    /// </remarks>
    public DateTimeOffset? SyncedAt { get; set; }
}
