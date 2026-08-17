namespace GameLauncher.Desktop.Services.Discovery.Normalization;

/// <summary>
/// Collapses the genre vocabularies of different sources into one set.
/// </summary>
/// <remarks>
/// <para>
/// Sources disagree on spelling more than on meaning: MobyGames writes
/// <c>Role-Playing (RPG)</c>, other sites write <c>RPG</c>, and both mean the
/// same shelf. Without a mapping the facet list ends up with several entries
/// that filter to overlapping sets, which is worse than having no facets.
/// </para>
/// <para>
/// Unrecognised genres are kept rather than discarded, tidied to title case.
/// Discarding them would silently lose a real distinction, and because genres are
/// stored as normalised rows an unknown one simply becomes a new row that can be
/// mapped here later and corrected everywhere at once.
/// </para>
/// </remarks>
public static class GenreVocabulary
{
    /// <summary>Separators that divide a list of genres. A slash does not: "Racing / Driving" is one genre.</summary>
    private static readonly char[] Separators = [',', ';', '|'];

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["action"] = "Action",
        ["adventure"] = "Adventure",
        ["action adventure"] = "Adventure",
        ["arcade"] = "Arcade",
        ["educational"] = "Educational",
        ["edutainment"] = "Educational",
        ["fighting"] = "Fighting",
        ["beat 'em up"] = "Fighting",
        ["beat em up"] = "Fighting",
        ["platform"] = "Platform",
        ["platformer"] = "Platform",
        ["puzzle"] = "Puzzle",
        ["racing"] = "Racing",
        ["driving"] = "Racing",
        ["racing / driving"] = "Racing",
        ["racing/driving"] = "Racing",
        ["rpg"] = "Role-Playing",
        ["role playing"] = "Role-Playing",
        ["role-playing"] = "Role-Playing",
        ["role-playing (rpg)"] = "Role-Playing",
        ["shooter"] = "Shooter",
        ["fps"] = "Shooter",
        ["first person shooter"] = "Shooter",
        ["shoot 'em up"] = "Shooter",
        ["shoot em up"] = "Shooter",
        ["simulation"] = "Simulation",
        ["sim"] = "Simulation",
        ["sports"] = "Sports",
        ["strategy"] = "Strategy",
        ["strategy / tactics"] = "Strategy",
        ["real-time strategy"] = "Strategy",
        ["turn-based strategy"] = "Strategy",
        ["managerial / business simulation"] = "Simulation",
        ["flight simulator"] = "Simulation",
        ["trivia"] = "Puzzle",
        ["board game"] = "Puzzle",
        ["card game"] = "Puzzle",
        ["compilation"] = "Compilation"
    };

    /// <summary>
    /// Splits and maps a source's genre string.
    /// </summary>
    /// <param name="raw">The value as the source gave it, possibly a list.</param>
    /// <returns>Canonical genres, deduplicated, in the order first seen.</returns>
    /// <remarks>
    /// The Internet Archive stores multiple genres in one comma-separated field
    /// — <c>"Educational, Simulation"</c> is a real value — so splitting is part
    /// of mapping rather than the caller's problem.
    /// </remarks>
    public static IReadOnlyList<string> Map(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var seen = new List<string>();

        foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var mapped = MapOne(part);

            if (mapped is not null && !seen.Contains(mapped, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(mapped);
            }
        }

        return seen;
    }

    /// <summary>
    /// Maps only values that are recognised genres, discarding anything else.
    /// </summary>
    /// <param name="values">Candidate values, typically free-form tags.</param>
    /// <returns>Recognised genres, deduplicated, in the order first seen.</returns>
    /// <remarks>
    /// Used where the input is a general-purpose tag field rather than a genre
    /// field. Most Internet Archive items carry no curated genre but do carry
    /// subjects, some of which are genres and most of which are not — keeping
    /// the unrecognised ones would fill the genre facet with "dosbox",
    /// "emulation" and "msdos".
    /// </remarks>
    public static IReadOnlyList<string> MapKnown(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        var seen = new List<string>();

        foreach (var part in values.SelectMany(value =>
                     value?.Split(Separators, StringSplitOptions.RemoveEmptyEntries) ?? []))
        {
            if (Synonyms.TryGetValue(part.Trim(), out var canonical) &&
                !seen.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(canonical);
            }
        }

        return seen;
    }

    /// <summary>
    /// Maps several raw genre values at once.
    /// </summary>
    /// <param name="values">Raw values, each possibly a list.</param>
    /// <returns>Canonical genres, deduplicated, in the order first seen.</returns>
    public static IReadOnlyList<string> MapMany(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        var seen = new List<string>();

        foreach (var mapped in values.SelectMany(Map))
        {
            if (!seen.Contains(mapped, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(mapped);
            }
        }

        return seen;
    }

    /// <summary>Maps a single genre token.</summary>
    /// <param name="value">One genre.</param>
    /// <returns>The canonical form, or <see langword="null"/> when nothing survives trimming.</returns>
    private static string? MapOne(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        return Synonyms.TryGetValue(trimmed, out var canonical) ? canonical : ToTitleCase(trimmed);
    }

    /// <summary>Tidies an unrecognised genre so it at least presents consistently.</summary>
    /// <param name="value">The genre to tidy.</param>
    /// <returns>The value in title case.</returns>
    private static string ToTitleCase(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index];
            words[index] = char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        }

        return string.Join(' ', words);
    }
}
