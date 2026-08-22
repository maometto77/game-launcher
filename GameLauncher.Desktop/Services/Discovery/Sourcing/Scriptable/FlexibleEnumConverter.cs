using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// Reads an enum from a manifest in whichever spelling its author used.
/// </summary>
/// <remarks>
/// <para>
/// <c>direct-link</c>, <c>directLink</c>, <c>DirectLink</c> and
/// <c>direct_link</c> all mean the same value. Hyphens are what the documented
/// manifest format uses and what anyone writing YAML by hand reaches for, and
/// the default conversion accepts none of them: it hands the scalar to
/// <see cref="Enum.Parse(Type, string, bool)"/>, which knows only the member's
/// own name.
/// </para>
/// <para>
/// Worth being forgiving here specifically. These files are written by hand,
/// often once, by someone reading an example rather than a schema — and the
/// failure is not a helpful message but a manifest that silently does not load,
/// because a value that will not convert throws while the file is being read.
/// </para>
/// </remarks>
public sealed class FlexibleEnumConverter : IYamlTypeConverter
{
    /// <inheritdoc />
    public bool Accepts(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return (Nullable.GetUnderlyingType(type) ?? type).IsEnum;
    }

    /// <inheritdoc />
    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(type);

        var target = Nullable.GetUnderlyingType(type) ?? type;
        var scalar = parser.Consume<Scalar>();
        var value = scalar.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return Nullable.GetUnderlyingType(type) is not null
                ? null
                : Activator.CreateInstance(target);
        }

        var wanted = Simplify(value);

        foreach (var name in Enum.GetNames(target))
        {
            if (Simplify(name).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse(target, name);
            }
        }

        // Named rather than swallowed. A value nobody recognises is a mistake
        // worth reporting, and the alternative — quietly using the first member
        // — would give a manifest behaviour its author never asked for.
        throw new YamlException(
            scalar.Start,
            scalar.End,
            $"'{value}' is not one of: {string.Join(", ", Enum.GetNames(target).Select(Hyphenate))}.");
    }

    /// <inheritdoc />
    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.Emit(new Scalar(value is null ? string.Empty : Hyphenate(value.ToString()!)));
    }

    /// <summary>Reduces a name to the form spellings are compared in.</summary>
    /// <param name="value">The name as written.</param>
    /// <returns>Letters and digits only, lower-cased.</returns>
    private static string Simplify(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Writes a member name in the hyphenated form the format uses.</summary>
    /// <param name="name">The member name.</param>
    /// <returns>The hyphenated spelling.</returns>
    private static string Hyphenate(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);

        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[index]));
        }

        return builder.ToString();
    }
}
