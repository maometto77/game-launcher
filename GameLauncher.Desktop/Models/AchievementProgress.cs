namespace GameLauncher.Desktop.Models;

/// <summary>
/// Current progress towards an achievement that has not yet been earned.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="AchievementUnlock"/>. The two have
/// opposite lifecycles: progress is mutable and rewritten as often as the source
/// value changes, whereas an unlock is written once and never revised. Keeping
/// them apart means the permanent record is never touched by the hot path.
/// </remarks>
public sealed class AchievementProgress
{
    /// <summary>Identifier of the <see cref="AchievementDefinition"/> being progressed.</summary>
    public int DefinitionId { get; set; }

    /// <summary>
    /// The most recently observed value, measured against
    /// <see cref="AchievementDefinition.ProgressTarget"/>.
    /// </summary>
    public double CurrentValue { get; set; }

    /// <summary>When this progress was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Calculates completion as a fraction between zero and one.
    /// </summary>
    /// <param name="target">The achievement's target value.</param>
    /// <returns>
    /// The clamped ratio of <see cref="CurrentValue"/> to <paramref name="target"/>,
    /// or zero when the target is absent or not positive.
    /// </returns>
    /// <remarks>
    /// Clamped so that a stat overshooting its target — a counter that kept
    /// climbing after the achievement was earned — reports as complete rather
    /// than as more than complete.
    /// </remarks>
    public double GetCompletion(double? target) =>
        target is > 0 ? Math.Clamp(CurrentValue / target.Value, 0d, 1d) : 0d;
}
