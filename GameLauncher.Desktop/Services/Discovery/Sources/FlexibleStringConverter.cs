using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Desktop.Services.Discovery.Sources;

/// <summary>
/// Reads a field that a source returns as a string on one item and an array on
/// the next.
/// </summary>
/// <remarks>
/// <para>
/// The Internet Archive's search index does this with <c>title</c>: an item
/// carrying two titles returns both in an array, and every other item returns a
/// bare string. Typed as a plain string, one such item throws — and because a
/// page is deserialised in a single pass, it takes every other result on that
/// page with it.
/// </para>
/// <para>
/// The first entry is taken, which is what the Archive means by the primary
/// value. Numbers are accepted too: the same index returns <c>year</c> as a
/// number on some items and a string on others.
/// </para>
/// </remarks>
internal sealed class FlexibleStringConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number)
                    ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);

            case JsonTokenType.Null:
                return null;

            case JsonTokenType.StartArray:
                string? first = null;

                // The whole array is consumed even once a value is found: leaving
                // the reader mid-array would corrupt every field after it.
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (first is null && reader.TokenType == JsonTokenType.String)
                    {
                        first = reader.GetString();
                    }
                    else if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    {
                        reader.Skip();
                    }
                }

                return first;

            default:
                reader.Skip();
                return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>Only ever read from a response; writing is not part of the contract.</remarks>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value);
    }
}
