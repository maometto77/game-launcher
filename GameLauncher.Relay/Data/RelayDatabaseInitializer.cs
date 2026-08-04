using System.Data;
using System.Globalization;
using Dapper;

namespace GameLauncher.Relay.Data;

/// <summary>
/// Creates and migrates the relay schema.
/// </summary>
/// <remarks>
/// <para>
/// Every statement here is valid on both SQLite and PostgreSQL. Three rules make
/// that possible, and breaking any of them is what would tie the relay to one
/// engine:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>No auto-increment identity columns.</b> This is where the dialects
/// genuinely diverge. Every key is a value the application generates — a friend
/// code, a catalog id, a device id, or a composite of natural keys.
/// </description></item>
/// <item><description>
/// <b>No defaults on boolean or timestamp columns.</b> PostgreSQL rejects
/// <c>DEFAULT 0</c> on a boolean, and the two engines spell "now" differently.
/// Values are always supplied by the application, which is also easier to test.
/// </description></item>
/// <item><description>
/// <b>Only shared column types</b> — <c>TEXT</c>, <c>INTEGER</c>,
/// <c>BOOLEAN</c>, <c>DOUBLE PRECISION</c>. Timestamps are UTC ISO-8601 text,
/// which orders lexicographically exactly as it does chronologically.
/// </description></item>
/// </list>
/// </remarks>
public sealed class RelayDatabaseInitializer
{
    private readonly IRelayConnectionFactory _connectionFactory;
    private readonly ILogger<RelayDatabaseInitializer> _logger;

    /// <summary>
    /// Ordered migrations. Index + 1 is the version each produces.
    /// </summary>
    private static readonly string[] Migrations =
    [
        // v1 — identity, friendships, presence, catalog, achievements.
        """
        -- Named AppUser rather than User: "user" is a reserved word in
        -- PostgreSQL, and a table that needs quoting everywhere it appears is a
        -- standing invitation to get it wrong once.
        CREATE TABLE IF NOT EXISTS AppUser (
            FriendCode  TEXT NOT NULL PRIMARY KEY,
            DisplayName TEXT NOT NULL,
            CreatedAt   TEXT NOT NULL,
            UpdatedAt   TEXT NOT NULL
        );

        -- One user, many devices, from the start. The friend code identifies the
        -- person; the token identifies the machine. Splitting these later would
        -- mean invalidating every token already issued.
        CREATE TABLE IF NOT EXISTS Device (
            DeviceId   TEXT NOT NULL PRIMARY KEY,
            FriendCode TEXT NOT NULL,
            TokenHash  TEXT NOT NULL,
            Label      TEXT NOT NULL,
            CreatedAt  TEXT NOT NULL,
            LastSeenAt TEXT NOT NULL,
            RevokedAt  TEXT NULL,

            CONSTRAINT FK_Device_User FOREIGN KEY (FriendCode)
                REFERENCES AppUser (FriendCode) ON DELETE CASCADE
        );

        -- The authentication lookup: hash the presented token, find the device.
        -- Unique because a hash collision would be an authentication bypass.
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Device_TokenHash ON Device (TokenHash);
        CREATE INDEX IF NOT EXISTS IX_Device_FriendCode ON Device (FriendCode);

        -- One row per relationship, created by the requester. The pair is ordered
        -- (requester, addressee) so a request has a direction; acceptance sets
        -- Status rather than creating a second row.
        CREATE TABLE IF NOT EXISTS Friendship (
            UserFriendCode   TEXT NOT NULL,
            FriendFriendCode TEXT NOT NULL,
            Status           INTEGER NOT NULL,
            CreatedAt        TEXT NOT NULL,
            RespondedAt      TEXT NULL,

            CONSTRAINT PK_Friendship PRIMARY KEY (UserFriendCode, FriendFriendCode),

            CONSTRAINT FK_Friendship_User FOREIGN KEY (UserFriendCode)
                REFERENCES AppUser (FriendCode) ON DELETE CASCADE,
            CONSTRAINT FK_Friendship_Friend FOREIGN KEY (FriendFriendCode)
                REFERENCES AppUser (FriendCode) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_Friendship_Friend ON Friendship (FriendFriendCode);

        -- Keyed on the person, not the device: a user with two machines online
        -- should appear once to their friends, not twice.
        CREATE TABLE IF NOT EXISTS Presence (
            FriendCode          TEXT NOT NULL PRIMARY KEY,
            CurrentGameTitle    TEXT NULL,
            CurrentGameCatalogId TEXT NULL,
            IsOnline            BOOLEAN NOT NULL,
            LastSeenAt          TEXT NOT NULL,

            CONSTRAINT FK_Presence_User FOREIGN KEY (FriendCode)
                REFERENCES AppUser (FriendCode) ON DELETE CASCADE
        );

        -- Shared identity of a game title. Rows are never deleted and CatalogId
        -- is never rewritten; a merge sets SupersededByCatalogId instead.
        CREATE TABLE IF NOT EXISTS CatalogEntry (
            CatalogId             TEXT NOT NULL PRIMARY KEY,
            CanonicalTitle        TEXT NOT NULL,
            Company               TEXT NULL,
            SupersededByCatalogId TEXT NULL,
            CreatedAt             TEXT NOT NULL,
            UpdatedAt             TEXT NOT NULL,

            CONSTRAINT FK_CatalogEntry_Superseded FOREIGN KEY (SupersededByCatalogId)
                REFERENCES CatalogEntry (CatalogId)
        );

        CREATE INDEX IF NOT EXISTS IX_CatalogEntry_Superseded
            ON CatalogEntry (SupersededByCatalogId);

        -- Many fingerprints resolve to one title: a re-release, a different
        -- publisher's build, a launcher executable versus the game's own.
        CREATE TABLE IF NOT EXISTS CatalogAlias (
            Fingerprint TEXT NOT NULL PRIMARY KEY,
            CatalogId   TEXT NOT NULL,
            CreatedAt   TEXT NOT NULL,

            CONSTRAINT FK_CatalogAlias_Entry FOREIGN KEY (CatalogId)
                REFERENCES CatalogEntry (CatalogId)
        );

        CREATE INDEX IF NOT EXISTS IX_CatalogAlias_CatalogId ON CatalogAlias (CatalogId);

        -- Keyed on ApiName rather than any definition row id. A row id belongs to
        -- whichever database produced it and a catalog merge may delete one; the
        -- api name is the stable authored handle and survives both. This is what
        -- makes a merge a data-movement problem instead of a history-loss one.
        CREATE TABLE IF NOT EXISTS UserAchievement (
            FriendCode TEXT NOT NULL,
            CatalogId  TEXT NOT NULL,
            ApiName    TEXT NOT NULL,
            UnlockedAt TEXT NOT NULL,

            CONSTRAINT PK_UserAchievement PRIMARY KEY (FriendCode, CatalogId, ApiName),

            CONSTRAINT FK_UserAchievement_User FOREIGN KEY (FriendCode)
                REFERENCES AppUser (FriendCode) ON DELETE CASCADE,
            CONSTRAINT FK_UserAchievement_Catalog FOREIGN KEY (CatalogId)
                REFERENCES CatalogEntry (CatalogId)
        );

        CREATE INDEX IF NOT EXISTS IX_UserAchievement_Catalog ON UserAchievement (CatalogId);

        -- Which titles a user has. Supports "your friend plays this too" and the
        -- denominator for achievement rarity.
        CREATE TABLE IF NOT EXISTS UserLibrary (
            FriendCode TEXT NOT NULL,
            CatalogId  TEXT NOT NULL,
            AddedAt    TEXT NOT NULL,

            CONSTRAINT PK_UserLibrary PRIMARY KEY (FriendCode, CatalogId),

            CONSTRAINT FK_UserLibrary_User FOREIGN KEY (FriendCode)
                REFERENCES AppUser (FriendCode) ON DELETE CASCADE,
            CONSTRAINT FK_UserLibrary_Catalog FOREIGN KEY (CatalogId)
                REFERENCES CatalogEntry (CatalogId)
        );

        CREATE INDEX IF NOT EXISTS IX_UserLibrary_Catalog ON UserLibrary (CatalogId);

        -- Relay-level facts, of which the instance identity is the first.
        --
        -- Deliberately stored in the database rather than in configuration: the
        -- identity must travel with the data. Moving the relay to another host,
        -- or restoring it from a backup, then keeps the same identity, and
        -- clients correctly treat it as the same relay. A configured value would
        -- change whenever somebody edited a file.
        CREATE TABLE IF NOT EXISTS RelayMetadata (
            Key   TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL
        );
        """
    ];

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies connections to migrate.</param>
    /// <param name="logger">Logger for migration diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RelayDatabaseInitializer(
        IRelayConnectionFactory connectionFactory,
        ILogger<RelayDatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the schema version this build expects.</summary>
    public static int TargetVersion => Migrations.Length;

    /// <summary>
    /// Creates the database if absent and applies outstanding migrations.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    /// <exception cref="InvalidOperationException">
    /// The database was created by a newer build.
    /// </exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RelayTypeHandlers.Register();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // A version table rather than SQLite's user_version pragma, which has no
        // PostgreSQL equivalent.
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var current = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(Version) FROM SchemaVersion;",
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;

        if (current > TargetVersion)
        {
            throw new InvalidOperationException(
                $"The relay database is at schema version {current}, but this build understands " +
                $"{TargetVersion}. Deploy a newer relay build before starting this one.");
        }

        if (current == TargetVersion)
        {
            _logger.LogDebug("Relay schema is current at version {Version}.", current);
            return;
        }

        _logger.LogInformation("Migrating relay schema from version {From} to {To}.", current, TargetVersion);

        for (var index = current; index < TargetVersion; index++)
        {
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    Migrations[index], transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO SchemaVersion (Version) VALUES (@Version);",
                    new { Version = index + 1 }, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Applied relay schema migration {Version}.", index + 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relay schema migration {Version} failed; rolling back.", index + 1);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        _logger.LogInformation("Relay schema is now at version {Version}.", TargetVersion);
    }

    /// <summary>Metadata key holding this relay's instance identity.</summary>
    public const string RelayIdKey = "relay_id";

    /// <summary>
    /// Returns this relay's instance identity, generating it on first call.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The stable relay identity.</returns>
    /// <remarks>
    /// Written with <c>ON CONFLICT DO NOTHING</c> and then read back, so two
    /// requests racing on a cold start converge on one value rather than each
    /// believing it minted the identity.
    /// </remarks>
    public async Task<string> GetOrCreateRelayIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO RelayMetadata (Key, Value)
            VALUES (@Key, @Value)
            ON CONFLICT (Key) DO NOTHING;
            """,
            new { Key = RelayIdKey, Value = "rly_" + Guid.NewGuid().ToString("N") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT Value FROM RelayMetadata WHERE Key = @Key;",
            new { Key = RelayIdKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The relay identity could not be established.");
    }
}

/// <summary>
/// Dapper type handlers for the relay's portable storage conventions.
/// </summary>
public static class RelayTypeHandlers
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>
    /// Installs the handlers. Safe to call repeatedly.
    /// </summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            SqlMapper.AddTypeHandler(new UtcTimestampHandler());
            _registered = true;
        }
    }

    /// <summary>
    /// Stores <see cref="DateTimeOffset"/> as UTC ISO-8601 text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Normalised to UTC before writing, which is the whole point: a
    /// UTC ISO-8601 string sorts lexicographically in exactly the same order as
    /// it does chronologically, so <c>MIN</c>, <c>MAX</c> and <c>ORDER BY</c> are
    /// correct on both SQLite and PostgreSQL without any conversion.
    /// </para>
    /// <para>
    /// Deliberately different from the desktop client, which keeps the original
    /// offset because it displays local times. The relay only ever compares and
    /// orders, and preserving offsets there would break lexicographic ordering.
    /// </para>
    /// </remarks>
    public sealed class UtcTimestampHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        /// <summary>Fixed-width round-trip format; fixed width is what keeps sorting correct.</summary>
        private const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        /// <inheritdoc />
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            parameter.DbType = DbType.String;
            parameter.Value = value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset stamp => stamp.ToUniversalTime(),
            DateTime stamp => new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc)),
            string text => DateTimeOffset.Parse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            _ => throw new FormatException($"Cannot read a timestamp from '{value}'.")
        };
    }
}
