using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Desktop.Services.Achievements.Configuration;

/// <summary>
/// Trigger configuration for a save-file achievement.
/// </summary>
/// <remarks>
/// Serialised into <see cref="Models.AchievementDefinition.TriggerConfigJson"/>
/// in the shape
/// <c>{ "saveFilePath": "", "format": "json|xml|ini|regex", "fieldPath": "",
/// "comparison": "gte|eq|contains", "value": "" }</c>.
/// </remarks>
public sealed record SaveFileTriggerConfig
{
    /// <summary>Absolute path to the save file to inspect.</summary>
    [JsonPropertyName("saveFilePath")]
    public string SaveFilePath { get; init; } = string.Empty;

    /// <summary>Which parser to use for the file.</summary>
    [JsonPropertyName("format")]
    [JsonConverter(typeof(SaveFileFormatConverter))]
    public SaveFileFormat Format { get; init; } = SaveFileFormat.Json;

    /// <summary>
    /// Where the value sits inside the file. Interpretation depends on
    /// <see cref="Format"/>: a dotted path for JSON, an XPath expression for
    /// XML, <c>section/key</c> for INI, and a regular expression whose first
    /// capture group holds the value for <see cref="SaveFileFormat.Regex"/>.
    /// </summary>
    [JsonPropertyName("fieldPath")]
    public string FieldPath { get; init; } = string.Empty;

    /// <summary>How the extracted value is compared against <see cref="Value"/>.</summary>
    [JsonPropertyName("comparison")]
    [JsonConverter(typeof(ComparisonOperatorConverter))]
    public ComparisonOperator Comparison { get; init; } = ComparisonOperator.GreaterThanOrEqual;

    /// <summary>
    /// The target the extracted value is compared against, held as text.
    /// </summary>
    /// <remarks>
    /// Kept as a string because a save file may hold a number, a flag or a name,
    /// and the comparison decides how to interpret it: numeric where both sides
    /// parse as numbers, textual otherwise.
    /// </remarks>
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// Checks that this configuration is complete enough to evaluate.
    /// </summary>
    /// <param name="error">Set to a user-facing reason when validation fails.</param>
    /// <returns><see langword="true"/> when the configuration can be evaluated.</returns>
    public bool Validate(out string? error)
    {
        if (string.IsNullOrWhiteSpace(SaveFilePath))
        {
            error = "A save file path is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(FieldPath))
        {
            error = "A field path is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Value))
        {
            error = "A target value is required.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Parses a configuration from stored JSON.
    /// </summary>
    /// <param name="json">The stored configuration text.</param>
    /// <returns>The parsed configuration, or <see langword="null"/> when the text is absent or malformed.</returns>
    /// <remarks>
    /// Returns null rather than throwing so that one hand-edited definition
    /// cannot stop every other achievement from being evaluated.
    /// </remarks>
    public static SaveFileTriggerConfig? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SaveFileTriggerConfig>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
