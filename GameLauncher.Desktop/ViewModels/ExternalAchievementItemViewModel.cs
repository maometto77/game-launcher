using System.Globalization;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One achievement or statistic read off the disk, ready to render.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <see cref="AchievementItemViewModel"/>. That one renders a
/// curated definition and knows about hidden achievements, provider
/// availability and the editor; none of those exist here. What this has instead
/// is a source to attribute and an API name standing in for a title, because
/// these files record identifiers rather than display names.
/// </para>
/// <para>
/// Culture-aware where a person reads it and invariant where a machine does:
/// the unlock date is formatted for the reader, the identifier is not
/// case-folded by their locale.
/// </para>
/// </remarks>
public sealed class ExternalAchievementItemViewModel
{
    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="achievement">The row to render.</param>
    /// <param name="sourceName">What to call the writer that recorded it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="achievement"/> is <see langword="null"/>.</exception>
    public ExternalAchievementItemViewModel(ExternalAchievement achievement, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(achievement);

        Achievement = achievement;
        SourceName = sourceName ?? achievement.SourceKey;
    }

    /// <summary>Gets the row being rendered.</summary>
    public ExternalAchievement Achievement { get; }

    /// <summary>Gets the name of the writer that recorded it.</summary>
    public string SourceName { get; }

    /// <summary>Gets the achievement's API name, which is all these files carry.</summary>
    public string Title => Achievement.ApiName;

    /// <summary>Gets a value indicating whether it has been earned.</summary>
    public bool IsUnlocked => Achievement.IsUnlocked;

    /// <summary>Gets a value indicating whether this row is a statistic rather than an achievement.</summary>
    public bool IsStatistic => Achievement.Kind == ExternalAchievementKind.Statistic;

    /// <summary>Gets the badge shown beside the name.</summary>
    public string StateLabel => IsStatistic
        ? "Stat"
        : IsUnlocked ? "Unlocked" : "Locked";

    /// <summary>Gets when it was earned, as text.</summary>
    /// <remarks>
    /// A row can be unlocked with no time: several writers store the flag and not
    /// the timestamp, and inventing one would be a lie about when it happened.
    /// </remarks>
    public string UnlockedText => Achievement.UnlockedAt is { } stamp
        ? stamp.LocalDateTime.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)
        : IsUnlocked ? "Unlocked" : string.Empty;

    /// <summary>Gets a value indicating whether a progress bar should be drawn.</summary>
    public bool HasProgress => !IsUnlocked && Achievement.Fraction is not null;

    /// <summary>Gets progress as a percentage, for the bar.</summary>
    public double ProgressPercent => (Achievement.Fraction ?? 0) * 100;

    /// <summary>Gets progress as text, or the raw value for a statistic with no target.</summary>
    public string ProgressText
    {
        get
        {
            if (Achievement.CurrentValue is not { } current)
            {
                return string.Empty;
            }

            return Achievement.TargetValue is > 0
                ? $"{Format(current)} of {Format(Achievement.TargetValue.Value)}"
                : Format(current);
        }
    }

    /// <summary>Gets the attribution line shown under the name.</summary>
    public string DetailText => IsStatistic
        ? $"{SourceName} · {ProgressText}"
        : UnlockedText is { Length: > 0 } unlocked
            ? $"{SourceName} · {unlocked}"
            : SourceName;

    /// <summary>Formats a value the way a counter should read.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    /// Whole numbers lose their decimal point: a kill count of <c>4213</c> should
    /// read as <c>4,213</c> rather than <c>4213.00</c>.
    /// </remarks>
    private static string Format(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0001
            ? Math.Round(value).ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString("N2", CultureInfo.CurrentCulture);
}
