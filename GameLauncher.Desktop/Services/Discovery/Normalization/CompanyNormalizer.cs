using System.Text.RegularExpressions;

namespace GameLauncher.Desktop.Services.Discovery.Normalization;

/// <summary>
/// Collapses the ways sources spell a developer or publisher.
/// </summary>
/// <remarks>
/// <para>
/// One company is written <c>MicroProse</c>, <c>MicroProse Software, Inc.</c> and
/// <c>MICROPROSE</c> across sources and across years. Because developers and
/// publishers are stored as normalised rows, letting those stand as three
/// separate entities would give three facet entries that each filter to part of
/// the same catalogue.
/// </para>
/// <para>
/// Only legal-entity suffixes are removed — <c>Inc</c>, <c>Ltd</c>, <c>GmbH</c>
/// and their kin. Descriptive words such as <c>Software</c>, <c>Games</c> and
/// <c>Entertainment</c> are deliberately kept: stripping them would fold
/// genuinely different companies that share a first word into one, and a wrong
/// merge here misattributes an entire back catalogue.
/// </para>
/// </remarks>
public static partial class CompanyNormalizer
{
    /// <summary>
    /// Tidies a company name for display.
    /// </summary>
    /// <param name="value">The name as a source gave it.</param>
    /// <returns>The trimmed name, or <see langword="null"/> when nothing survives.</returns>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // A trailing comma or semicolon is stray punctuation from a list; a
        // trailing full stop is nearly always part of an abbreviation, and
        // stripping it turns "Accolade, Inc." into something the source did not
        // say. Only the former is removed.
        var cleaned = WhitespacePattern().Replace(value, " ").Trim().TrimEnd(',', ';').Trim();

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Reduces a company name to the key used to recognise it again.
    /// </summary>
    /// <param name="value">The name as a source gave it.</param>
    /// <returns>A lowercase, suffix-free, unpunctuated key, or an empty string.</returns>
    public static string Normalize(string? value)
    {
        var cleaned = Clean(value);

        if (cleaned is null)
        {
            return string.Empty;
        }

        var working = cleaned.ToLowerInvariant();

        working = EntitySuffixPattern().Replace(working, " ");
        working = PunctuationPattern().Replace(working, " ");

        return WhitespacePattern().Replace(working, " ").Trim();
    }

    /// <summary>Matches trailing legal-entity suffixes, with or without punctuation.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(
        @"[,\s]+(?:inc|incorporated|ltd|limited|llc|l\.l\.c|corp|corporation|co|gmbh|ag|s\.?a|b\.?v|pty|plc)\b\.?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EntitySuffixPattern();

    /// <summary>Matches anything that is not a letter, digit or space.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"[^\p{L}\p{Nd} ]+", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationPattern();

    /// <summary>Matches runs of whitespace.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
