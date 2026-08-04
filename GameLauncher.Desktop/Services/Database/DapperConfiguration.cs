using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Registers the Dapper type handlers the schema relies on.
/// </summary>
/// <remarks>
/// SQLite has no native date or array type. Rather than let each repository
/// invent its own encoding, the mapping is declared once here so every read and
/// write agrees on the storage format.
/// </remarks>
public static class DapperConfiguration
{
    private static readonly object Gate = new();
    private static bool _initialised;

    /// <summary>
    /// Installs the type handlers. Safe to call more than once; subsequent calls
    /// do nothing.
    /// </summary>
    /// <remarks>
    /// Dapper's handler table is process-wide static state, so registering twice
    /// would be wasted work and registering from two threads at once would race.
    /// </remarks>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialised)
            {
                return;
            }

            // Columns are named exactly as the properties are, so Dapper's
            // underscore matching would only add ambiguity.
            DefaultTypeMap.MatchNamesWithUnderscores = false;

            SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
            SqlMapper.AddTypeHandler(typeof(IReadOnlyList<string>), new StringListHandler());

            _initialised = true;
        }
    }

    /// <summary>
    /// Stores <see cref="DateTimeOffset"/> as a round-trippable ISO-8601 string.
    /// </summary>
    /// <remarks>
    /// The <c>"O"</c> format preserves the UTC offset, so a value written in one
    /// time zone and read in another still denotes the same instant. Storing a
    /// Unix timestamp would lose the original offset, which matters for
    /// displaying when a session actually happened in local terms.
    /// </remarks>
    public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        /// <inheritdoc />
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            parameter.DbType = DbType.String;
            parameter.Value = value.ToString("O", CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        /// <exception cref="FormatException">The stored text is not a valid timestamp.</exception>
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset stamp => stamp,
            DateTime stamp => new DateTimeOffset(stamp),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => throw new FormatException($"Cannot read a DateTimeOffset from '{value}'.")
        };
    }

    /// <summary>
    /// Stores a string list as a JSON array in a single text column.
    /// </summary>
    /// <remarks>
    /// JSON rather than a delimited string, so a tag containing a comma or
    /// semicolon survives a round trip intact.
    /// </remarks>
    public sealed class StringListHandler : SqlMapper.TypeHandler<IReadOnlyList<string>>
    {
        /// <inheritdoc />
        public override void SetValue(IDbDataParameter parameter, IReadOnlyList<string>? value)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            parameter.DbType = DbType.String;
            parameter.Value = JsonSerializer.Serialize(value ?? Array.Empty<string>());
        }

        /// <inheritdoc />
        public override IReadOnlyList<string> Parse(object? value)
        {
            if (value is not string text || string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<string[]>(text) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                // A hand-edited or corrupted column should cost the user their
                // tags, not their whole library: degrade to empty and carry on.
                return Array.Empty<string>();
            }
        }
    }
}
