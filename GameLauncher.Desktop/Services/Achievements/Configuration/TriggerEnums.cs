using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Desktop.Services.Achievements.Configuration;

/// <summary>
/// How a read value is compared against the achievement's target value.
/// </summary>
public enum ComparisonOperator
{
    /// <summary>Read value is greater than or equal to the target. Wire token <c>gte</c>.</summary>
    GreaterThanOrEqual = 0,

    /// <summary>Read value equals the target. Wire token <c>eq</c>.</summary>
    Equal = 1,

    /// <summary>Read value contains the target as a substring. Wire token <c>contains</c>.</summary>
    Contains = 2
}

/// <summary>
/// The parser used to read a value out of a save file.
/// </summary>
public enum SaveFileFormat
{
    /// <summary>JSON, addressed with a dotted path. Wire token <c>json</c>.</summary>
    Json = 0,

    /// <summary>XML, addressed with an XPath expression. Wire token <c>xml</c>.</summary>
    Xml = 1,

    /// <summary>INI, addressed as <c>section/key</c> or bare <c>key</c>. Wire token <c>ini</c>.</summary>
    Ini = 2,

    /// <summary>
    /// Raw regular expression over the file's text, used as a fallback for
    /// formats with no structured parser. Wire token <c>regex</c>.
    /// </summary>
    Regex = 3
}

/// <summary>
/// The primitive type to interpret bytes as when reading process memory.
/// </summary>
public enum MemoryValueType
{
    /// <summary>Signed 32-bit integer, four bytes. Wire token <c>int32</c>.</summary>
    Int32 = 0,

    /// <summary>Single-precision float, four bytes. Wire token <c>float</c>.</summary>
    Float = 1,

    /// <summary>Unsigned single byte. Wire token <c>byte</c>.</summary>
    Byte = 2
}

/// <summary>
/// Serialises an enum using an explicit token table.
/// </summary>
/// <typeparam name="TEnum">The enum being converted.</typeparam>
/// <remarks>
/// The trigger configuration format specifies exact tokens — <c>gte</c>,
/// <c>int32</c> — that no naming policy produces from the member names. .NET 8's
/// <see cref="JsonStringEnumConverter"/> cannot rename individual members
/// (<c>JsonStringEnumMemberName</c> arrived in .NET 9), so the mapping is
/// declared explicitly here. Reading is case-insensitive; writing always emits
/// the canonical token.
/// </remarks>
public abstract class TokenEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<string, TEnum> _fromToken;
    private readonly Dictionary<TEnum, string> _toToken;

    /// <summary>
    /// Initialises the converter with its token table.
    /// </summary>
    /// <param name="tokens">Map of wire token to enum member.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokens"/> is <see langword="null"/>.</exception>
    protected TokenEnumConverter(IReadOnlyDictionary<string, TEnum> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        _fromToken = new Dictionary<string, TEnum>(tokens, StringComparer.OrdinalIgnoreCase);
        _toToken = tokens.ToDictionary(pair => pair.Value, pair => pair.Key);
    }

    /// <inheritdoc />
    /// <exception cref="JsonException">The token is absent or not recognised.</exception>
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var token = reader.GetString();

        if (token is not null && _fromToken.TryGetValue(token, out var value))
        {
            return value;
        }

        throw new JsonException(
            $"'{token}' is not a valid {typeof(TEnum).Name}. Expected one of: {string.Join(", ", _fromToken.Keys)}.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(_toToken.TryGetValue(value, out var token) ? token : value.ToString());
    }
}

/// <summary>Converts <see cref="ComparisonOperator"/> to and from its wire tokens.</summary>
public sealed class ComparisonOperatorConverter : TokenEnumConverter<ComparisonOperator>
{
    /// <summary>Initialises a new instance.</summary>
    public ComparisonOperatorConverter()
        : base(new Dictionary<string, ComparisonOperator>
        {
            ["gte"] = ComparisonOperator.GreaterThanOrEqual,
            ["eq"] = ComparisonOperator.Equal,
            ["contains"] = ComparisonOperator.Contains
        })
    {
    }
}

/// <summary>Converts <see cref="SaveFileFormat"/> to and from its wire tokens.</summary>
public sealed class SaveFileFormatConverter : TokenEnumConverter<SaveFileFormat>
{
    /// <summary>Initialises a new instance.</summary>
    public SaveFileFormatConverter()
        : base(new Dictionary<string, SaveFileFormat>
        {
            ["json"] = SaveFileFormat.Json,
            ["xml"] = SaveFileFormat.Xml,
            ["ini"] = SaveFileFormat.Ini,
            ["regex"] = SaveFileFormat.Regex
        })
    {
    }
}

/// <summary>Converts <see cref="MemoryValueType"/> to and from its wire tokens.</summary>
public sealed class MemoryValueTypeConverter : TokenEnumConverter<MemoryValueType>
{
    /// <summary>Initialises a new instance.</summary>
    public MemoryValueTypeConverter()
        : base(new Dictionary<string, MemoryValueType>
        {
            ["int32"] = MemoryValueType.Int32,
            ["float"] = MemoryValueType.Float,
            ["byte"] = MemoryValueType.Byte
        })
    {
    }
}
