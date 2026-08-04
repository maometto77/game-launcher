using System.Globalization;
using GameLauncher.Desktop.Services.Achievements.Configuration;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// Compares an observed value against an achievement's target.
/// </summary>
/// <remarks>
/// Shared by every provider that reads a value and compares it, so that
/// <c>gte</c> means the same thing whether the number came from a save file or
/// from process memory.
/// </remarks>
public static class AchievementComparison
{
    /// <summary>
    /// Determines whether an observed value satisfies a comparison.
    /// </summary>
    /// <param name="observed">The value that was read.</param>
    /// <param name="comparison">How to compare it.</param>
    /// <param name="target">The achievement's target value.</param>
    /// <returns><see langword="true"/> when the condition is met.</returns>
    /// <remarks>
    /// Numeric when both sides parse as numbers, textual otherwise. That matters
    /// because a save file holding <c>"100"</c> and one holding <c>100</c> should
    /// behave identically, while <c>"gte"</c> against a level name should still
    /// fall back to something sensible rather than throwing.
    /// </remarks>
    public static bool Satisfies(string? observed, ComparisonOperator comparison, string? target)
    {
        observed ??= string.Empty;
        target ??= string.Empty;

        if (comparison == ComparisonOperator.Contains)
        {
            return observed.Contains(target, StringComparison.OrdinalIgnoreCase);
        }

        if (TryParseNumber(observed, out var observedNumber) && TryParseNumber(target, out var targetNumber))
        {
            return comparison switch
            {
                ComparisonOperator.GreaterThanOrEqual => observedNumber >= targetNumber,

                // Compared with a tolerance because a float read from memory
                // almost never equals a decimal typed by a human exactly.
                ComparisonOperator.Equal => Math.Abs(observedNumber - targetNumber) < 1e-6,
                _ => false
            };
        }

        return comparison switch
        {
            ComparisonOperator.Equal => string.Equals(observed, target, StringComparison.OrdinalIgnoreCase),

            // Ordinal ordering is a poor stand-in for "greater than or equal" on
            // text, but it is defined and predictable, which beats failing.
            ComparisonOperator.GreaterThanOrEqual =>
                string.Compare(observed, target, StringComparison.OrdinalIgnoreCase) >= 0,
            _ => false
        };
    }

    /// <summary>
    /// Parses a value as a number for progress reporting.
    /// </summary>
    /// <param name="value">The value to parse.</param>
    /// <returns>The number, or <see langword="null"/> when it is not numeric.</returns>
    /// <remarks>
    /// Used so the interface can show partial progress. A value that is not a
    /// number simply has no progress to show, which is not a failure.
    /// </remarks>
    public static double? AsProgress(string? value) =>
        TryParseNumber(value, out var number) ? number : null;

    /// <summary>Parses a number using invariant culture.</summary>
    /// <param name="value">Text to parse.</param>
    /// <param name="number">Receives the parsed value.</param>
    /// <returns><see langword="true"/> when the text is numeric.</returns>
    /// <remarks>
    /// Invariant on purpose: a save file's contents do not change with the
    /// player's regional settings, so parsing them with the current culture would
    /// make the same file evaluate differently on two machines.
    /// </remarks>
    private static bool TryParseNumber(string? value, out double number) =>
        double.TryParse(
            (value ?? string.Empty).Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number);
}
