using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// Default <see cref="ISaveFileReader"/>.
/// </summary>
/// <remarks>
/// Every read opens the file with <see cref="FileShare.ReadWrite"/>. A running
/// game usually holds its save file open, and a reader that demanded exclusive
/// access would fail for exactly the achievements most worth evaluating.
/// </remarks>
public sealed class SaveFileReader : ISaveFileReader
{
    /// <summary>
    /// Upper bound on the file size this will read into memory.
    /// </summary>
    /// <remarks>
    /// Save files are small. The limit exists so that a mis-configured rule
    /// pointing at a multi-gigabyte archive fails politely instead of exhausting
    /// memory.
    /// </remarks>
    private const long MaximumFileBytes = 32 * 1024 * 1024;

    /// <summary>Bound on regular expression evaluation, guarding against catastrophic backtracking.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<SaveFileReader> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="logger">Logger for read diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public SaveFileReader(ILogger<SaveFileReader> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public SaveFileReadResult ReadValue(string filePath, SaveFileFormat format, string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return SaveFileReadResult.Failure("No save file path is configured.");
        }

        if (!File.Exists(filePath))
        {
            // Entirely normal before the player has saved.
            return SaveFileReadResult.Failure($"The save file does not exist: {filePath}");
        }

        string content;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > MaximumFileBytes)
            {
                return SaveFileReadResult.Failure(
                    $"The save file is larger than {MaximumFileBytes / (1024 * 1024)} MB and was not read.");
            }

            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveFileReadResult.Failure($"The save file could not be read: {ex.Message}");
        }

        try
        {
            return format switch
            {
                SaveFileFormat.Json => ReadJson(content, fieldPath),
                SaveFileFormat.Xml => ReadXml(content, fieldPath),
                SaveFileFormat.Ini => ReadIni(content, fieldPath),
                SaveFileFormat.Regex => ReadRegex(content, fieldPath),
                _ => SaveFileReadResult.Failure($"Unsupported save file format '{format}'.")
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Parsing {Path} as {Format} failed.", filePath, format);
            return SaveFileReadResult.Failure($"The save file could not be parsed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a value from JSON using a dotted path.
    /// </summary>
    /// <param name="content">The document text.</param>
    /// <param name="fieldPath">Dotted path, supporting array indices such as <c>slots[2].score</c>.</param>
    /// <returns>The value, or the reason it was not found.</returns>
    internal static SaveFileReadResult ReadJson(string content, string fieldPath)
    {
        using var document = JsonDocument.Parse(content);
        var current = document.RootElement;

        foreach (var segment in SplitPath(fieldPath))
        {
            if (segment.Index is { } index)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return SaveFileReadResult.Failure($"'{fieldPath}' has no element at index {index}.");
                }

                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment.Name!, out var next))
            {
                return SaveFileReadResult.Failure($"'{fieldPath}' was not found in the save file.");
            }

            current = next;
        }

        // Raw text for everything except strings, so numbers keep their exact
        // representation rather than passing through a double.
        return SaveFileReadResult.Success(current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => current.GetRawText()
        });
    }

    /// <summary>Reads a value from XML using an XPath expression.</summary>
    /// <param name="content">The document text.</param>
    /// <param name="fieldPath">XPath expression.</param>
    /// <returns>The value, or the reason it was not found.</returns>
    private static SaveFileReadResult ReadXml(string content, string fieldPath)
    {
        var document = new XmlDocument
        {
            // Save files are untrusted input. Resolving external entities would
            // let a crafted file read arbitrary local files or reach the network.
            XmlResolver = null
        };

        using var reader = XmlReader.Create(
            new StringReader(content),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

        document.Load(reader);

        var navigator = document.CreateNavigator()
                        ?? throw new InvalidOperationException("The XML document could not be navigated.");

        var result = navigator.Evaluate(fieldPath);

        return result switch
        {
            XPathNodeIterator iterator when iterator.MoveNext() =>
                SaveFileReadResult.Success(iterator.Current?.Value ?? string.Empty),

            XPathNodeIterator => SaveFileReadResult.Failure($"'{fieldPath}' matched nothing in the save file."),

            null => SaveFileReadResult.Failure($"'{fieldPath}' matched nothing in the save file."),

            // An XPath expression may also evaluate directly to a number, string
            // or boolean, as count() and string-length() do.
            _ => SaveFileReadResult.Success(Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    /// <summary>
    /// Reads a value from an INI file.
    /// </summary>
    /// <param name="content">The file text.</param>
    /// <param name="fieldPath"><c>section/key</c>, or a bare <c>key</c> for the unnamed leading section.</param>
    /// <returns>The value, or the reason it was not found.</returns>
    internal static SaveFileReadResult ReadIni(string content, string fieldPath)
    {
        var separator = fieldPath.LastIndexOfAny(['/', '\\']);

        var wantedSection = separator >= 0 ? fieldPath[..separator].Trim() : string.Empty;
        var wantedKey = (separator >= 0 ? fieldPath[(separator + 1)..] : fieldPath).Trim();

        if (wantedKey.Length == 0)
        {
            return SaveFileReadResult.Failure("An INI field path needs a key.");
        }

        var section = string.Empty;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            // Both comment markers are in common use, and a comment must never be
            // mistaken for a key.
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();

            if (!string.Equals(section, wantedSection, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(key, wantedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return SaveFileReadResult.Success(line[(equals + 1)..].Trim().Trim('"'));
        }

        return SaveFileReadResult.Failure($"'{fieldPath}' was not found in the save file.");
    }

    /// <summary>
    /// Reads a value by matching a regular expression against the raw text.
    /// </summary>
    /// <param name="content">The file text.</param>
    /// <param name="pattern">Pattern whose first capture group holds the value.</param>
    /// <returns>The value, or the reason it was not found.</returns>
    /// <remarks>
    /// The fallback for formats with no structured parser — a binary save with a
    /// readable header, or a bespoke text layout.
    /// </remarks>
    internal static SaveFileReadResult ReadRegex(string content, string pattern)
    {
        var match = Regex.Match(content, pattern, RegexOptions.None, RegexTimeout);

        if (!match.Success)
        {
            return SaveFileReadResult.Failure("The pattern matched nothing in the save file.");
        }

        // The first capture group when there is one, since that is where a rule
        // author puts the value; the whole match otherwise.
        return SaveFileReadResult.Success(
            match.Groups.Count > 1 ? match.Groups[1].Value : match.Value);
    }

    /// <summary>Splits a dotted JSON path into property and index segments.</summary>
    /// <param name="fieldPath">The path to split.</param>
    /// <returns>The segments, in order.</returns>
    private static IEnumerable<(string? Name, int? Index)> SplitPath(string fieldPath)
    {
        foreach (var part in fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part;

            var bracket = name.IndexOf('[');
            if (bracket < 0)
            {
                yield return (name, null);
                continue;
            }

            // "slots[2]" is a property followed by an index; "[2]" is just an index.
            if (bracket > 0)
            {
                yield return (name[..bracket], null);
            }

            var indices = name[bracket..];

            foreach (Match match in Regex.Matches(indices, @"\[(\d+)\]", RegexOptions.None, RegexTimeout))
            {
                yield return (null, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }
    }
}
