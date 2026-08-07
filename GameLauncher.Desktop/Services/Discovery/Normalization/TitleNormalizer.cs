using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLauncher.Desktop.Services.Discovery.Normalization;

/// <summary>
/// Turns the many ways sources spell a game's name into one comparable form.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static: no database, no network, no clock. Every rule here is a
/// judgement about when two strings mean the same game, and each one trades
/// recall against the risk of merging two genuinely different titles. The bias
/// throughout is towards <em>not</em> merging — a missed merge shows up as a
/// duplicate the user can see and report, whereas a wrong merge silently
/// destroys the distinction between a game and its sequel.
/// </para>
/// <para>
/// That is why version and edition markers are stripped but <c>demo</c>,
/// <c>shareware</c> and <c>beta</c> are not: those name a materially different
/// release, and collapsing them would attribute one release's downloads to
/// another.
/// </para>
/// </remarks>
public static partial class TitleNormalizer
{
    /// <summary>Articles moved or dropped when comparing titles.</summary>
    private static readonly string[] Articles = ["the", "a", "an"];

    /// <summary>
    /// Words that describe how a copy runs rather than which game it is.
    /// </summary>
    /// <remarks>
    /// A parenthesised group is dropped only when <em>every</em> word in it is
    /// one of these. Internet Archive titles routinely carry
    /// "(DOS) (Dosbox in Browser) (VGA,SB)", which says nothing about the game
    /// and would otherwise make the same title unmatchable across sources.
    /// Dropping parentheses wholesale would be wrong — "Command &amp; Conquer
    /// (Red Alert)" is a different game from "Command &amp; Conquer".
    /// </remarks>
    private static readonly HashSet<string> TechnicalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dos", "msdos", "ms", "pc", "win", "win3", "win32", "win16", "windows", "dosbox",
        "in", "browser", "emulated", "emulator", "vga", "svga", "ega", "cga", "sb",
        "adlib", "soundblaster", "sound", "blaster", "midi", "mt32", "floppy", "disk",
        "disc", "cd", "cdrom", "rom", "exe", "x86", "3dfx", "glide", "hdd"
    };

    /// <summary>
    /// Trailing markers that describe a packaging of the same game rather than a
    /// different game.
    /// </summary>
    private static readonly string[] EditionMarkers =
    [
        "game of the year edition",
        "collectors edition",
        "collector's edition",
        "anniversary edition",
        "definitive edition",
        "platinum edition",
        "enhanced edition",
        "special edition",
        "deluxe edition",
        "premium edition",
        "gold edition",
        "goty edition",
        "directors cut",
        "director's cut",
        "cd-rom version",
        "cd-rom release",
        "floppy version",
        "talkie version",
        "remastered",
        "cd version",
        "disk version",
        "cd release",
        "goty"
    ];

    /// <summary>
    /// Rewrites a catalogue-style trailing article back into reading order.
    /// </summary>
    /// <param name="title">The title as a source gave it.</param>
    /// <returns>The title in reading order.</returns>
    /// <remarks>
    /// Library catalogues file <c>The Oregon Trail</c> as <c>Oregon Trail, The</c>
    /// so it sorts under O, and the Internet Archive stores exactly that form.
    /// Displaying it unchanged would be visibly wrong, so it is undone on the way
    /// in rather than worked around at every display site.
    /// </remarks>
    public static string RestoreLeadingArticle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var trimmed = title.Trim();
        var comma = trimmed.LastIndexOf(',');

        if (comma <= 0 || comma == trimmed.Length - 1)
        {
            return trimmed;
        }

        var tail = trimmed[(comma + 1)..].Trim();

        return Articles.Contains(tail, StringComparer.OrdinalIgnoreCase)
            ? $"{tail} {trimmed[..comma].Trim()}"
            : trimmed;
    }

    /// <summary>
    /// Produces the form a title should sort under.
    /// </summary>
    /// <param name="title">A title in reading order.</param>
    /// <returns>The title with any leading article removed.</returns>
    public static string ToSortTitle(string title)
    {
        var restored = RestoreLeadingArticle(title);

        foreach (var article in Articles)
        {
            if (restored.StartsWith(article + " ", StringComparison.OrdinalIgnoreCase))
            {
                return restored[(article.Length + 1)..].Trim();
            }
        }

        return restored;
    }

    /// <summary>
    /// Reduces a title to the form used for duplicate detection.
    /// </summary>
    /// <param name="title">The title as a source gave it.</param>
    /// <returns>
    /// A lowercase, unpunctuated, article-free, diacritic-free form, or an empty
    /// string when nothing survives.
    /// </returns>
    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var working = ToSortTitle(title).ToLowerInvariant();

        working = StripDiacritics(working);
        working = TrailingYearPattern().Replace(working, " ");
        working = BracketedPattern().Replace(working, " ");
        working = StripTechnicalParentheticals(working);
        working = VersionPattern().Replace(working, " ");
        working = PunctuationPattern().Replace(working, " ");
        working = CollapseWhitespace(working);
        working = StripEditionMarkers(working);
        working = ConvertRomanNumerals(working);

        return CollapseWhitespace(working);
    }

    /// <summary>
    /// Builds the key two listings must share to be considered the same game.
    /// </summary>
    /// <param name="title">The title as a source gave it.</param>
    /// <param name="year">Release year, or <see langword="null"/> when unknown.</param>
    /// <returns>The match key.</returns>
    /// <remarks>
    /// The year is part of the key so that a remake does not silently absorb the
    /// original. Listings whose years disagree by a year still match, through the
    /// title-only lookup the matcher performs as a second pass — sources
    /// routinely differ on release versus re-release dates.
    /// </remarks>
    public static string ComputeMatchKey(string title, int? year) =>
        $"{Normalize(title)}|{year?.ToString(CultureInfo.InvariantCulture) ?? "0"}";

    /// <summary>
    /// Builds the year-independent key used for the second matching pass.
    /// </summary>
    /// <param name="title">The title as a source gave it.</param>
    /// <returns>The normalised title alone.</returns>
    public static string ComputeTitleKey(string title) => Normalize(title);

    /// <summary>
    /// Removes parenthesised groups that describe how a copy runs.
    /// </summary>
    /// <param name="value">A partly normalised title.</param>
    /// <returns>The value without its technical annotations.</returns>
    /// <remarks>
    /// A group survives unless every word in it is technical, so a parenthesised
    /// subtitle that happens to contain one such word is kept intact.
    /// </remarks>
    private static string StripTechnicalParentheticals(string value)
    {
        if (!value.Contains('(', StringComparison.Ordinal))
        {
            return value;
        }

        return ParentheticalPattern().Replace(value, match =>
        {
            var words = match.Groups[1].Value
                .Split([' ', ',', '/', '+', '-', '.'], StringSplitOptions.RemoveEmptyEntries);

            return words.Length > 0 && words.All(TechnicalWords.Contains) ? " " : match.Value;
        });
    }

    /// <summary>Removes accents so "Pokémon" and "Pokemon" compare equal.</summary>
    /// <param name="value">The value to fold.</param>
    /// <returns>The value with combining marks removed.</returns>
    private static string StripDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Removes trailing edition and packaging markers.</summary>
    /// <param name="value">A partly normalised title.</param>
    /// <returns>The value without a trailing edition marker.</returns>
    /// <remarks>
    /// Applied repeatedly because a title can carry more than one — "gold edition
    /// cd version" is a real shape. Only trailing markers are removed; one in the
    /// middle is part of the name.
    /// </remarks>
    private static string StripEditionMarkers(string value)
    {
        var working = value;
        bool removed;

        do
        {
            removed = false;

            foreach (var marker in EditionMarkers)
            {
                if (!working.EndsWith(" " + marker, StringComparison.Ordinal))
                {
                    continue;
                }

                working = working[..^(marker.Length + 1)].TrimEnd();
                removed = true;
                break;
            }
        }
        while (removed && working.Length > 0);

        return working;
    }

    /// <summary>Rewrites standalone roman numerals as digits.</summary>
    /// <param name="value">A partly normalised title.</param>
    /// <returns>The value with roman numeral tokens converted.</returns>
    /// <remarks>
    /// Only tokens of two characters or more are converted. A lone <c>i</c>,
    /// <c>v</c> or <c>x</c> is far more often a word, an initial or part of a
    /// stylised name than a number — "x com" must not become "10 com".
    /// </remarks>
    private static string ConvertRomanNumerals(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var changed = false;

        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Length < 2 || !TryParseRoman(tokens[index], out var number))
            {
                continue;
            }

            tokens[index] = number.ToString(CultureInfo.InvariantCulture);
            changed = true;
        }

        return changed ? string.Join(' ', tokens) : value;
    }

    /// <summary>Parses a lowercase roman numeral.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="value">The parsed value when successful.</param>
    /// <returns><see langword="true"/> when the token is a well-formed roman numeral.</returns>
    private static bool TryParseRoman(string token, out int value)
    {
        value = 0;

        if (!RomanPattern().IsMatch(token))
        {
            return false;
        }

        var total = 0;
        var previous = 0;

        for (var index = token.Length - 1; index >= 0; index--)
        {
            var digit = token[index] switch
            {
                'i' => 1,
                'v' => 5,
                'x' => 10,
                'l' => 50,
                'c' => 100,
                'd' => 500,
                'm' => 1000,
                _ => 0
            };

            if (digit == 0)
            {
                return false;
            }

            total += digit < previous ? -digit : digit;
            previous = digit;
        }

        value = total;
        return total > 0;
    }

    /// <summary>Collapses runs of whitespace and trims.</summary>
    /// <param name="value">The value to tidy.</param>
    /// <returns>The tidied value.</returns>
    private static string CollapseWhitespace(string value) =>
        WhitespacePattern().Replace(value, " ").Trim();

    /// <summary>Matches a trailing parenthesised four-digit year.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\((?:19|20)\d{2}\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearPattern();

    /// <summary>Matches square-bracketed annotations.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedPattern();

    /// <summary>Matches a parenthesised group and captures its contents.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\(([^()]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ParentheticalPattern();

    /// <summary>Matches version markers such as "v1.2" or "version 2".</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\b(?:v|ver|version)\s*\.?\s*\d+(?:\.\d+)*\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>Matches anything that is not a letter, digit or space.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"[^\p{L}\p{Nd} ]+", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationPattern();

    /// <summary>Matches runs of whitespace.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    /// <summary>Matches a well-formed lowercase roman numeral.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(
        "^m*(cm|cd|d?c{0,3})(xc|xl|l?x{0,3})(ix|iv|v?i{0,3})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RomanPattern();
}
