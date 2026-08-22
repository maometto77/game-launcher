using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// Reads fields out of a document, by selector or by inference.
/// </summary>
/// <remarks>
/// <para>
/// Three sources of truth, tried in that order: a selector the manifest named,
/// the structured metadata a page publishes about itself, and finally the
/// ordinary shape of an HTML document. The order is the whole design — an
/// explicit selector is what someone asked for, structured metadata is what the
/// site says about itself, and guessing from headings is a last resort that is
/// right often enough to be worth having and wrong often enough not to be
/// preferred.
/// </para>
/// <para>
/// The structured layer reads JSON-LD and OpenGraph. Both are published
/// deliberately, by sites that want to be indexed correctly, which makes them a
/// far better source than any heuristic and is why they are consulted before
/// the document's own markup.
/// </para>
/// </remarks>
public static partial class HtmlReader
{
    /// <summary>Longest description worth keeping.</summary>
    private const int MaxDescriptionLength = 4000;

    /// <summary>Longest single text field worth keeping.</summary>
    private const int MaxFieldLength = 400;

    /// <summary>Most values to take from a repeated field.</summary>
    private const int MaxListValues = 24;

    /// <summary>Headings a title is usually in, best first.</summary>
    private static readonly string[] TitleCandidates =
    [
        "h1.entry-title", "h1.post-title", "h1.game-title", "h1.title",
        ".entry-title", ".post-title", ".game-title",
        "article h1", "main h1", "h1",
        "article h2", "main h2",
    ];

    /// <summary>Places a description usually lives, best first.</summary>
    private static readonly string[] DescriptionCandidates =
    [
        ".entry-content p", ".post-content p", ".description", ".game-description",
        ".summary", ".excerpt", "article p", "main p",
    ];

    /// <summary>Places a cover usually lives, best first.</summary>
    private static readonly string[] CoverCandidates =
    [
        "img.cover", ".cover img", ".game-cover img", ".poster img", ".boxart img",
        ".entry-content img", ".post-content img", "article img", "main img",
    ];

    /// <summary>Labels that introduce a developer.</summary>
    private static readonly string[] DeveloperLabels = ["developer", "developed by", "author", "studio"];

    /// <summary>Labels that introduce a publisher.</summary>
    private static readonly string[] PublisherLabels = ["publisher", "published by"];

    /// <summary>Labels that introduce platforms.</summary>
    private static readonly string[] PlatformLabels = ["platform", "platforms", "os", "operating system"];

    /// <summary>Labels that introduce genres.</summary>
    private static readonly string[] GenreLabels = ["genre", "genres", "category", "categories", "tags"];

    /// <summary>Labels that introduce a release date.</summary>
    private static readonly string[] DateLabels = ["released", "release date", "year", "date"];

    [GeneratedRegex(@"\b(19[5-9]\d|20[0-4]\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    /// <summary>
    /// Reads a single text value.
    /// </summary>
    /// <param name="scope">The element or document to look in.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <param name="candidates">Selectors to try when none was named.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    public static string? Text(IParentNode? scope, string? selector, params string[] candidates)
    {
        if (scope is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            // An explicit selector is honoured even when it finds nothing. A
            // silent fallback would make a typo look like a site that changed.
            return Clean(Query(scope, selector)?.TextContent, MaxFieldLength);
        }

        foreach (var candidate in candidates)
        {
            if (Clean(Query(scope, candidate)?.TextContent, MaxFieldLength) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a title.
    /// </summary>
    /// <param name="document">The page.</param>
    /// <param name="scope">A listing entry, or <see langword="null"/> for the whole page.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <returns>The title, or <see langword="null"/>.</returns>
    public static string? Title(IDocument document, IParentNode? scope, string? selector)
    {
        ArgumentNullException.ThrowIfNull(document);

        var target = scope ?? document;

        if (!string.IsNullOrWhiteSpace(selector))
        {
            return Clean(Query(target, selector)?.TextContent, MaxFieldLength);
        }

        // Structured first: a site that publishes a name means it.
        if (scope is null && StructuredName(document) is { } declared)
        {
            return declared;
        }

        if (Text(target, null, TitleCandidates) is { } heading)
        {
            return heading;
        }

        // The document title, with the site name trimmed off the end. "Doom |
        // Example Games" is a title and a site, and only the first half is the
        // game.
        if (scope is null && Clean(document.Title, MaxFieldLength) is { } head)
        {
            return TrimSiteSuffix(head);
        }

        // A listing entry with no heading: its link text is the best guess left.
        return scope is null ? null : Clean(Query(scope, "a")?.TextContent, MaxFieldLength);
    }

    /// <summary>
    /// Reads a description.
    /// </summary>
    /// <param name="document">The page.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <returns>The description, or <see langword="null"/>.</returns>
    public static string? Description(IDocument document, string? selector)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!string.IsNullOrWhiteSpace(selector))
        {
            return Clean(Query(document, selector)?.TextContent, MaxDescriptionLength);
        }

        if (Structured(document, "description") is { } declared)
        {
            return Clean(declared, MaxDescriptionLength);
        }

        if (Meta(document, "og:description") is { } social)
        {
            return Clean(social, MaxDescriptionLength);
        }

        if (MetaName(document, "description") is { } standard)
        {
            return Clean(standard, MaxDescriptionLength);
        }

        foreach (var candidate in DescriptionCandidates)
        {
            if (Clean(Query(document, candidate)?.TextContent, MaxDescriptionLength) is { Length: > 40 } prose)
            {
                return prose;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an image address.
    /// </summary>
    /// <param name="document">The page.</param>
    /// <param name="baseAddress">The page's address, for relative forms.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <returns>The address, or <see langword="null"/>.</returns>
    public static Uri? Cover(IDocument document, Uri baseAddress, string? selector)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(baseAddress);

        if (!string.IsNullOrWhiteSpace(selector))
        {
            return ImageFrom(Query(document, selector), baseAddress);
        }

        // og:image exists so that a link preview shows the right picture, which
        // is the same picture a card wants.
        if (Meta(document, "og:image") is { } social &&
            UrlGuard.Canonicalize(social, baseAddress) is { } declared)
        {
            return declared;
        }

        foreach (var candidate in CoverCandidates)
        {
            if (ImageFrom(Query(document, candidate), baseAddress) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads screenshot addresses.
    /// </summary>
    /// <param name="document">The page.</param>
    /// <param name="baseAddress">The page's address, for relative forms.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <param name="exclude">An address already used as the cover.</param>
    /// <returns>The addresses found, capped.</returns>
    public static IReadOnlyList<Uri> Screenshots(
        IDocument document,
        Uri baseAddress,
        string? selector,
        Uri? exclude)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(baseAddress);

        var scope = string.IsNullOrWhiteSpace(selector)
            ? ".entry-content img, .post-content img, .screenshots img, .gallery img, article img"
            : selector;

        var found = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (exclude is not null)
        {
            seen.Add(exclude.AbsoluteUri);
        }

        foreach (var element in QueryAll(document, scope))
        {
            if (ImageFrom(element, baseAddress) is not { } address || !seen.Add(address.AbsoluteUri))
            {
                continue;
            }

            found.Add(address);

            if (found.Count >= MaxListValues)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Reads a release year.
    /// </summary>
    /// <param name="document">The page.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <returns>The year, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A year rather than a date, because that is what the catalogue matches on
    /// and what sites publish consistently. A <c>datetime</c> attribute is
    /// preferred over prose: it is machine-readable by design, whereas "Spring
    /// 1993" is not.
    /// </remarks>
    public static int? Year(IDocument document, string? selector)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!string.IsNullOrWhiteSpace(selector))
        {
            var element = Query(document, selector);

            return YearFrom(element?.GetAttribute("datetime")) ?? YearFrom(element?.TextContent);
        }

        foreach (var declared in new[]
                 {
                     Structured(document, "datePublished"),
                     Structured(document, "dateCreated"),
                     Meta(document, "article:published_time"),
                 })
        {
            if (YearFrom(declared) is { } published)
            {
                return published;
            }
        }

        if (Query(document, "time[datetime]")?.GetAttribute("datetime") is { } stamp &&
            YearFrom(stamp) is { } timed)
        {
            return timed;
        }

        return YearFrom(LabelledValue(document, DateLabels));
    }

    /// <summary>
    /// Reads a repeated text field, such as genres.
    /// </summary>
    /// <param name="document">The page.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <param name="labels">Labels that introduce the value when guessing.</param>
    /// <returns>The values found, capped and de-duplicated.</returns>
    public static IReadOnlyList<string> Values(IDocument document, string? selector, string[] labels)
    {
        ArgumentNullException.ThrowIfNull(document);

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            foreach (var part in Split(value))
            {
                if (seen.Add(part) && found.Count < MaxListValues)
                {
                    found.Add(part);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            foreach (var element in QueryAll(document, selector))
            {
                Add(element.TextContent);
            }

            return found;
        }

        Add(LabelledValue(document, labels));

        return found;
    }

    /// <summary>
    /// Reads one value that a label introduces.
    /// </summary>
    /// <param name="scope">The element or document to look in.</param>
    /// <param name="labels">Labels to accept, lower-cased.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Handles the two shapes sites actually use for a fact table: a definition
    /// list, and a row whose first cell is the label. Both are read by finding
    /// the label and taking what follows it, which is what a person does.
    /// </remarks>
    public static string? LabelledValue(IParentNode? scope, string[] labels)
    {
        if (scope is null || labels.Length == 0)
        {
            return null;
        }

        foreach (var term in QueryAll(scope, "dt"))
        {
            if (!IsLabel(term.TextContent, labels))
            {
                continue;
            }

            // The matching definition is the next dd sibling.
            var definition = term.NextElementSibling;

            while (definition is not null && !definition.LocalName.Equals("dd", StringComparison.Ordinal))
            {
                definition = definition.NextElementSibling;
            }

            if (Clean(definition?.TextContent, MaxFieldLength) is { } value)
            {
                return value;
            }
        }

        foreach (var row in QueryAll(scope, "tr"))
        {
            var cells = row.Children
                .Where(child => child.LocalName is "td" or "th")
                .ToArray();

            if (cells.Length >= 2 &&
                IsLabel(cells[0].TextContent, labels) &&
                Clean(cells[1].TextContent, MaxFieldLength) is { } value)
            {
                return value;
            }
        }

        // "Developer: id Software" as a single run of text.
        foreach (var element in QueryAll(scope, "li, p, span, div"))
        {
            var text = Clean(element.TextContent, MaxFieldLength);

            if (text is null || text.Length > 200)
            {
                continue;
            }

            var separator = text.IndexOf(':', StringComparison.Ordinal);

            if (separator > 0 &&
                IsLabel(text[..separator], labels) &&
                Clean(text[(separator + 1)..], MaxFieldLength) is { } tail)
            {
                return tail;
            }
        }

        return null;
    }

    /// <summary>Gets the labels that introduce a developer.</summary>
    public static string[] DeveloperLabelSet => DeveloperLabels;

    /// <summary>Gets the labels that introduce a publisher.</summary>
    public static string[] PublisherLabelSet => PublisherLabels;

    /// <summary>Gets the labels that introduce platforms.</summary>
    public static string[] PlatformLabelSet => PlatformLabels;

    /// <summary>Gets the labels that introduce genres.</summary>
    public static string[] GenreLabelSet => GenreLabels;

    /// <summary>
    /// Runs a selector, treating a malformed one as finding nothing.
    /// </summary>
    /// <param name="scope">The element or document to search.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The first match, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A selector out of a hand-written manifest can be nonsense, and AngleSharp
    /// throws on one it cannot parse. Swallowed here so a typo in one field does
    /// not abandon a whole crawl.
    /// </remarks>
    public static IElement? Query(IParentNode? scope, string? selector)
    {
        if (scope is null || string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        try
        {
            return scope.QuerySelector(selector);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a selector for every match, treating a malformed one as finding none.
    /// </summary>
    /// <param name="scope">The element or document to search.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The matches, possibly empty.</returns>
    public static IReadOnlyList<IElement> QueryAll(IParentNode? scope, string? selector)
    {
        if (scope is null || string.IsNullOrWhiteSpace(selector))
        {
            return [];
        }

        try
        {
            return scope.QuerySelectorAll(selector).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Reads an OpenGraph property.</summary>
    /// <param name="document">The page.</param>
    /// <param name="property">The property name.</param>
    /// <returns>Its content, or <see langword="null"/>.</returns>
    public static string? Meta(IDocument document, string property) =>
        Clean(
            Query(document, $"meta[property='{property}']")?.GetAttribute("content"),
            MaxDescriptionLength);

    /// <summary>Reads a named meta tag.</summary>
    /// <param name="document">The page.</param>
    /// <param name="name">The tag name.</param>
    /// <returns>Its content, or <see langword="null"/>.</returns>
    public static string? MetaName(IDocument document, string name) =>
        Clean(
            Query(document, $"meta[name='{name}']")?.GetAttribute("content"),
            MaxDescriptionLength);

    /// <summary>
    /// Reads a property out of the page's JSON-LD, if it has any.
    /// </summary>
    /// <param name="document">The page.</param>
    /// <param name="property">The property to read.</param>
    /// <returns>Its value as text, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Only the shapes that occur in practice: an object, an array of them, or
    /// an <c>@graph</c> wrapper. A malformed block is skipped rather than
    /// reported, because a page with broken structured data is common and is not
    /// a reason to fail an import.
    /// </remarks>
    public static string? Structured(IDocument document, string property)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var script in QueryAll(document, "script[type='application/ld+json']"))
        {
            var text = script.TextContent;

            if (string.IsNullOrWhiteSpace(text) || text.Length > 512 * 1024)
            {
                continue;
            }

            JsonDocument parsed;

            try
            {
                parsed = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                continue;
            }

            using (parsed)
            {
                if (FindProperty(parsed.RootElement, property, 0) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>Reads a name out of structured data.</summary>
    /// <param name="document">The page.</param>
    /// <returns>The name, or <see langword="null"/>.</returns>
    private static string? StructuredName(IDocument document) =>
        Clean(Structured(document, "name"), MaxFieldLength) ?? Meta(document, "og:title");

    /// <summary>Walks a JSON value looking for a property.</summary>
    /// <param name="element">The value to search.</param>
    /// <param name="property">The property name.</param>
    /// <param name="depth">How deep this call is.</param>
    /// <returns>Its value as text, or <see langword="null"/>.</returns>
    private static string? FindProperty(JsonElement element, string property, int depth)
    {
        // Bounded, because structured data is untrusted input like everything
        // else on the page and nothing stops it nesting a thousand deep.
        if (depth > 6)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (member.NameEquals(property))
                    {
                        var value = Scalar(member.Value);

                        if (value is not null)
                        {
                            return value;
                        }
                    }
                }

                foreach (var member in element.EnumerateObject())
                {
                    if (FindProperty(member.Value, property, depth + 1) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindProperty(item, property, depth + 1) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Reads a JSON value as text, when it is one.</summary>
    /// <param name="element">The value.</param>
    /// <returns>Its text, or <see langword="null"/>.</returns>
    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.ToString(),

        // A nested object frequently carries the value under 'name', which is
        // how schema.org expresses "the publisher is this organisation".
        JsonValueKind.Object when element.TryGetProperty("name", out var name) => name.GetString(),
        JsonValueKind.Array => element.EnumerateArray()
            .Select(Scalar)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
        _ => null
    };

    /// <summary>Reads an image address out of an element.</summary>
    /// <param name="element">The element, expected to be an image.</param>
    /// <param name="baseAddress">The page's address, for relative forms.</param>
    /// <returns>The address, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Lazy-loading attributes are read as well as <c>src</c>, because a site
    /// that defers its images leaves <c>src</c> holding a placeholder and the
    /// real address in <c>data-src</c>.
    /// </remarks>
    private static Uri? ImageFrom(IElement? element, Uri baseAddress)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var attribute in new[] { "src", "data-src", "data-lazy-src", "data-original", "content" })
        {
            if (element.GetAttribute(attribute) is { } value &&
                !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                UrlGuard.Canonicalize(value, baseAddress) is { } address &&
                address.Scheme is "http" or "https")
            {
                return address;
            }
        }

        // A srcset holds several sizes; the first entry is enough for a card.
        if (element.GetAttribute("srcset") is { } srcset)
        {
            var first = srcset.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (UrlGuard.Canonicalize(first, baseAddress) is { } address &&
                address.Scheme is "http" or "https")
            {
                return address;
            }
        }

        return null;
    }

    /// <summary>Reads a four-digit year out of text.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The year, or <see langword="null"/>.</returns>
    private static int? YearFrom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = YearPattern().Match(value);

        return match.Success &&
               int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    /// <summary>Determines whether a cell's text is one of a set of labels.</summary>
    /// <param name="text">The cell's text.</param>
    /// <param name="labels">Labels to accept.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    private static bool IsLabel(string? text, string[] labels)
    {
        var cleaned = Clean(text, 64)?.TrimEnd(':', ' ').ToLowerInvariant();

        return cleaned is not null && labels.Contains(cleaned, StringComparer.Ordinal);
    }

    /// <summary>Splits a value that may hold several comma-separated entries.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The parts, trimmed.</returns>
    private static IEnumerable<string> Split(string? value)
    {
        if (Clean(value, MaxFieldLength) is not { } cleaned)
        {
            return [];
        }

        return cleaned
            .Split([',', '/', '|', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.Length is > 1 and <= 64);
    }

    /// <summary>
    /// Trims a site name off the end of a document title.
    /// </summary>
    /// <param name="title">The document title.</param>
    /// <returns>The part that is likely the game's name.</returns>
    private static string TrimSiteSuffix(string title)
    {
        foreach (var separator in new[] { " | ", " – ", " — ", " - ", " :: " })
        {
            var index = title.LastIndexOf(separator, StringComparison.Ordinal);

            // Only when what remains is still substantial: "Doom - Example" is
            // a game and a site, but "Command - Conquer" is a game.
            if (index > 8 && title.Length - index - separator.Length < index)
            {
                return title[..index].Trim();
            }
        }

        return title;
    }

    /// <summary>
    /// Collapses whitespace and enforces a length.
    /// </summary>
    /// <param name="value">The raw text.</param>
    /// <param name="maxLength">Longest value to keep.</param>
    /// <returns>The cleaned text, or <see langword="null"/> when nothing survives.</returns>
    /// <remarks>
    /// HTML text arrives with the source file's indentation in it. A length cap
    /// as well, because an element's text content is everything inside it and a
    /// selector aimed one level too high otherwise returns the whole page.
    /// </remarks>
    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = WhitespacePattern().Replace(value, " ").Trim();

        if (collapsed.Length == 0)
        {
            return null;
        }

        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength].TrimEnd() + "…";
    }
}
