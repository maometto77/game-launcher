using System.Globalization;
using System.Text.Json;

namespace GameLauncher.Desktop.Services.Discovery.Sources;

/// <summary>
/// Reads the Internet Archive's item metadata, which is only loosely typed.
/// </summary>
/// <remarks>
/// <para>
/// Deserialising this into a fixed class does not work. The same field arrives
/// as a string on one item and an array on the next — <c>collection</c>,
/// <c>subject</c> and <c>creator</c> all do it, depending on how many values the
/// item happens to have. Items also carry arbitrary extra fields, and the
/// <c>mobygames_*</c> block is present on curated items and absent elsewhere.
/// </para>
/// <para>
/// So the document is read as elements and interrogated field by field, with
/// every accessor tolerating "string, array of strings, number, or missing".
/// That is the shape of the data, not a shortcut around modelling it.
/// </para>
/// </remarks>
internal sealed class InternetArchiveMetadata
{
    private readonly JsonElement _root;
    private readonly JsonElement _metadata;

    /// <summary>
    /// Initialises a new instance over a parsed metadata document.
    /// </summary>
    /// <param name="root">The whole response.</param>
    private InternetArchiveMetadata(JsonElement root)
    {
        _root = root;

        _metadata = root.TryGetProperty("metadata", out var metadata)
            ? metadata
            : default;
    }

    /// <summary>Gets a value indicating whether the document described a real item.</summary>
    public bool IsPresent => _metadata.ValueKind == JsonValueKind.Object;

    /// <summary>Gets the item's own identifier.</summary>
    public string Identifier => GetString("identifier") ?? string.Empty;

    /// <summary>Gets the media type, which should be <c>software</c> for a game.</summary>
    public string? MediaType => GetString("mediatype");

    /// <summary>Gets the collections the item belongs to.</summary>
    public IReadOnlyList<string> Collections => GetStrings("collection");

    /// <summary>
    /// Gets a value indicating whether the Archive itself restricts downloading.
    /// </summary>
    /// <remarks>
    /// Both signals matter. <c>access-restricted-item</c> is the explicit flag,
    /// and membership of <c>stream_only</c> says the item may be played in the
    /// browser but not taken away. Offering either for install produces a 403,
    /// which is a worse experience than an explained absence.
    /// </remarks>
    public bool IsDownloadRestricted =>
        string.Equals(GetString("access-restricted-item"), "true", StringComparison.OrdinalIgnoreCase) ||
        Collections.Contains("stream_only", StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets when the Archive last changed the item.</summary>
    public DateTimeOffset? LastUpdated =>
        _root.TryGetProperty("item_last_updated", out var value) && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>Gets the item's files.</summary>
    public IReadOnlyList<InternetArchiveFile> Files
    {
        get
        {
            if (!_root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var parsed = new List<InternetArchiveFile>();

            foreach (var file in files.EnumerateArray())
            {
                var name = ReadString(file, "name");

                if (!string.IsNullOrEmpty(name))
                {
                    parsed.Add(new InternetArchiveFile(
                        name,
                        ReadString(file, "source"),
                        ReadString(file, "format"),
                        ReadString(file, "md5"),
                        ReadString(file, "sha1"),
                        ReadLong(file, "size")));
                }
            }

            return parsed;
        }
    }

    /// <summary>Gets the primary download host, or <see langword="null"/>.</summary>
    public string? PrimaryHost => ReadString(_root, "d1");

    /// <summary>Gets the secondary download host, or <see langword="null"/>.</summary>
    public string? SecondaryHost => ReadString(_root, "d2");

    /// <summary>Gets the directory the item's files live in on those hosts.</summary>
    public string? Directory => ReadString(_root, "dir");

    /// <summary>
    /// Parses a metadata response.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <returns>The parsed document, or <see langword="null"/> when it is not readable.</returns>
    public static InternetArchiveMetadata? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            // Cloned because the document is disposed here and the element would
            // otherwise reference freed memory.
            return new InternetArchiveMetadata(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a metadata field as a single string.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or <see langword="null"/> when absent or empty.</returns>
    /// <remarks>An array yields its first entry, which is what the Archive means by it.</remarks>
    public string? GetString(string name)
    {
        if (_metadata.ValueKind != JsonValueKind.Object || !_metadata.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Trim(value.GetString()),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(entry => Trim(entry.ToString()))
                .FirstOrDefault(entry => entry is not null),
            _ => null
        };
    }

    /// <summary>
    /// Reads a metadata field as a list of strings.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>Every value, or an empty list.</returns>
    public IReadOnlyList<string> GetStrings(string name)
    {
        if (_metadata.ValueKind != JsonValueKind.Object || !_metadata.TryGetProperty(name, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(entry => Trim(entry.ToString()))
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .ToArray();
        }

        var single = Trim(value.ToString());

        return single is null ? [] : [single];
    }

    /// <summary>
    /// Reads a release year from whichever field carries one.
    /// </summary>
    /// <returns>The year, or <see langword="null"/> when none is stated.</returns>
    /// <remarks>
    /// Tried in order of reliability. <c>year</c> is a plain year when present;
    /// <c>mobygames_released</c> is curated; <c>date</c> is an ISO date or a bare
    /// year. <c>publicdate</c> and <c>addeddate</c> are deliberately not used:
    /// they say when the Archive received the upload, not when the game came out,
    /// and mistaking one for the other would put 2014 on a 1990 game.
    /// </remarks>
    public int? GetYear()
    {
        foreach (var field in new[] { "year", "mobygames_released", "date" })
        {
            var value = GetString(field);

            if (value is null)
            {
                continue;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                return year;
            }

            // "1990-01-01" and "1990-01" both start with the year.
            if (value.Length >= 4 &&
                int.TryParse(value[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>Reads a string property from an arbitrary element.</summary>
    /// <param name="element">The element to read from.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? Trim(value.ToString())
            : null;

    /// <summary>Reads a numeric property that the Archive encodes as a string.</summary>
    /// <param name="element">The element to read from.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static long? ReadLong(JsonElement element, string name)
    {
        var raw = ReadString(element, name);

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Trims a value and treats blank as absent.</summary>
    /// <param name="value">The value to trim.</param>
    /// <returns>The trimmed value, or <see langword="null"/>.</returns>
    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// One file belonging to an Internet Archive item.
/// </summary>
/// <param name="Name">File name within the item.</param>
/// <param name="Source">
/// <c>original</c> for an uploaded file, <c>derivative</c> for one the Archive
/// generated.
/// </param>
/// <param name="Format">Format label, such as <c>ZIP</c>.</param>
/// <param name="Md5">MD5 digest as hex, or <see langword="null"/>.</param>
/// <param name="Sha1">SHA-1 digest as hex, or <see langword="null"/>.</param>
/// <param name="Size">Size in bytes, or <see langword="null"/>.</param>
internal sealed record InternetArchiveFile(
    string Name,
    string? Source,
    string? Format,
    string? Md5,
    string? Sha1,
    long? Size)
{
    /// <summary>Gets a value indicating whether this is an uploaded file rather than a derivative.</summary>
    public bool IsOriginal => string.Equals(Source, "original", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the file's extension in lower case, including the dot.</summary>
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
}
