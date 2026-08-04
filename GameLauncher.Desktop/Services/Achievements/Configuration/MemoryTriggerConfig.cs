using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Desktop.Services.Achievements.Configuration;

/// <summary>
/// Trigger configuration for a memory achievement.
/// </summary>
/// <remarks>
/// Serialised into <see cref="Models.AchievementDefinition.TriggerConfigJson"/>
/// in the shape
/// <c>{ "moduleName": "game.exe", "offset": "0x123456",
/// "valueType": "int32|float|byte", "comparison": "gte|eq", "value": "100" }</c>.
/// The offset is relative to the named module's base address, so it stays valid
/// across runs despite address space layout randomisation.
/// </remarks>
public sealed record MemoryTriggerConfig
{
    /// <summary>
    /// Name of the module the offset is relative to, for example
    /// <c>game.exe</c> or an engine DLL.
    /// </summary>
    [JsonPropertyName("moduleName")]
    public string ModuleName { get; init; } = string.Empty;

    /// <summary>
    /// Offset from the module's base address, as hexadecimal text such as
    /// <c>0x0012F3A0</c>.
    /// </summary>
    /// <remarks>
    /// Held as text rather than a number because that is how offsets are written
    /// and shared, and round-tripping through the editor should not silently
    /// reformat what the user typed.
    /// </remarks>
    [JsonPropertyName("offset")]
    public string Offset { get; init; } = "0x0";

    /// <summary>How to interpret the bytes at the resolved address.</summary>
    [JsonPropertyName("valueType")]
    [JsonConverter(typeof(MemoryValueTypeConverter))]
    public MemoryValueType ValueType { get; init; } = MemoryValueType.Int32;

    /// <summary>How the read value is compared against <see cref="Value"/>.</summary>
    [JsonPropertyName("comparison")]
    [JsonConverter(typeof(ComparisonOperatorConverter))]
    public ComparisonOperator Comparison { get; init; } = ComparisonOperator.GreaterThanOrEqual;

    /// <summary>The target value, held as text and parsed per <see cref="ValueType"/>.</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    /// <summary>Number of bytes that must be read for <see cref="ValueType"/>.</summary>
    public int ByteCount => ValueType switch
    {
        MemoryValueType.Int32 => sizeof(int),
        MemoryValueType.Float => sizeof(float),
        MemoryValueType.Byte => sizeof(byte),
        _ => sizeof(int)
    };

    /// <summary>
    /// Parses <see cref="Offset"/> into a numeric offset.
    /// </summary>
    /// <param name="offset">Receives the parsed offset when parsing succeeds.</param>
    /// <returns><see langword="true"/> when the text is a valid offset.</returns>
    /// <remarks>
    /// Accepts an optional <c>0x</c> prefix and is always interpreted as
    /// hexadecimal, which is how memory offsets are universally written. Parsing
    /// a prefix-less value as decimal would make <c>100</c> mean two different
    /// addresses depending on whether the user typed the prefix.
    /// </remarks>
    public bool TryGetOffset(out long offset)
    {
        offset = 0;

        var text = Offset?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset)
               && offset >= 0;
    }

    /// <summary>
    /// Checks that this configuration is complete enough to evaluate.
    /// </summary>
    /// <param name="error">Set to a user-facing reason when validation fails.</param>
    /// <returns><see langword="true"/> when the configuration can be evaluated.</returns>
    public bool Validate(out string? error)
    {
        if (string.IsNullOrWhiteSpace(ModuleName))
        {
            error = "A module name is required, for example game.exe.";
            return false;
        }

        if (!TryGetOffset(out _))
        {
            error = $"'{Offset}' is not a valid hexadecimal offset.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Value))
        {
            error = "A target value is required.";
            return false;
        }

        if (Comparison == ComparisonOperator.Contains)
        {
            error = "'contains' cannot be used for a memory achievement; use 'gte' or 'eq'.";
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
    public static MemoryTriggerConfig? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MemoryTriggerConfig>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
