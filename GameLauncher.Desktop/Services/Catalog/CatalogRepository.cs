using System.Data.Common;
using Dapper;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Catalog;

/// <summary>
/// Dapper-backed <see cref="ICatalogRepository"/>.
/// </summary>
public sealed class CatalogRepository : ICatalogRepository
{
    /// <summary>SQLite's extended result code for a primary key violation.</summary>
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>SQLite's extended result code for a unique index violation.</summary>
    private const int SqliteConstraintUnique = 2067;

    /// <summary>
    /// Maximum redirect hops followed when resolving a canonical entry.
    /// </summary>
    /// <remarks>
    /// Merge chains are short in practice. The bound exists so that a cycle —
    /// which a faulty relay or a hand-edited database could introduce — fails
    /// loudly instead of hanging the caller forever.
    /// </remarks>
    private const int MaximumRedirectHops = 16;

    private const string SelectColumns = """
        SELECT CatalogId, Source, IsProvisional, CanonicalTitle, MatchFingerprint,
               CreatedAt, UpdatedAt, SyncedAt, SupersededByCatalogId
        FROM   CatalogEntry
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<CatalogRepository> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <param name="logger">Logger for persistence diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CatalogRepository(IDbConnectionFactory connectionFactory, ILogger<CatalogRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<CatalogEntry>(
            new CommandDefinition(
                $"{SelectColumns} ORDER BY CanonicalTitle COLLATE NOCASE;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<CatalogEntry?> GetByIdAsync(string catalogId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetByIdAsync(connection, catalogId, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CatalogEntry?> ResolveCanonicalAsync(
        string catalogId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, catalogId, null, cancellationToken).ConfigureAwait(false);

        for (var hop = 0; current?.SupersededByCatalogId is { } next && hop < MaximumRedirectHops; hop++)
        {
            current = await GetByIdAsync(connection, next, null, cancellationToken).ConfigureAwait(false);
        }

        if (current?.SupersededByCatalogId is not null)
        {
            _logger.LogError(
                "Catalog redirect chain from {CatalogId} exceeded {Hops} hops; treating {Current} as canonical.",
                catalogId, MaximumRedirectHops, current.CatalogId);
        }

        return current;
    }

    /// <inheritdoc />
    public async Task<CatalogEntry?> FindByFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var catalogId = await connection.QueryFirstOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT CatalogId FROM CatalogAlias WHERE Fingerprint = @Fingerprint;",
                new { Fingerprint = fingerprint },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (string.IsNullOrEmpty(catalogId))
        {
            return null;
        }

        // Resolved rather than returned raw: the alias may point at an entry that
        // has since been merged into another.
        return await ResolveCanonicalAsync(catalogId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogEntry>> GetProvisionalAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<CatalogEntry>(
            new CommandDefinition(
                $"{SelectColumns} WHERE IsProvisional = 1 AND SupersededByCatalogId IS NULL ORDER BY CreatedAt;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task AddAsync(CatalogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO CatalogEntry
                    (CatalogId, Source, IsProvisional, CanonicalTitle, MatchFingerprint,
                     CreatedAt, UpdatedAt, SyncedAt, SupersededByCatalogId)
                VALUES
                    (@CatalogId, @Source, @IsProvisional, @CanonicalTitle, @MatchFingerprint,
                     @CreatedAt, @UpdatedAt, @SyncedAt, @SupersededByCatalogId);
                """,
                entry, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // The originating fingerprint becomes the entry's first alias, so that
            // fingerprint lookups have exactly one place to look.
            if (!string.IsNullOrWhiteSpace(entry.MatchFingerprint))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT OR IGNORE INTO CatalogAlias (Fingerprint, CatalogId, Source, CreatedAt)
                    VALUES (@Fingerprint, @CatalogId, @Source, @CreatedAt);
                    """,
                    new
                    {
                        Fingerprint = entry.MatchFingerprint,
                        entry.CatalogId,
                        entry.Source,
                        entry.CreatedAt
                    },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "Created catalog entry {CatalogId} for {Title}.", entry.CatalogId, entry.CanonicalTitle);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(CatalogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        entry.UpdatedAt = DateTimeOffset.Now;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE CatalogEntry
                SET    Source                = @Source,
                       IsProvisional         = @IsProvisional,
                       CanonicalTitle        = @CanonicalTitle,
                       MatchFingerprint      = @MatchFingerprint,
                       UpdatedAt             = @UpdatedAt,
                       SyncedAt              = @SyncedAt,
                       SupersededByCatalogId = @SupersededByCatalogId
                WHERE  CatalogId = @CatalogId;
                """,
                entry,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> AddAliasAsync(
        string fingerprint,
        string catalogId,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // OR IGNORE, not OR REPLACE: a fingerprint already bound to a title must
        // not be silently rebound by a later observation. Rebinding is a merge,
        // and merges are explicit.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT OR IGNORE INTO CatalogAlias (Fingerprint, CatalogId, Source, CreatedAt)
                VALUES (@Fingerprint, @CatalogId, @Source, @CreatedAt);
                """,
                new
                {
                    Fingerprint = fingerprint,
                    CatalogId = catalogId,
                    Source = source,
                    CreatedAt = DateTimeOffset.Now
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogAlias>> GetAliasesAsync(
        string catalogId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<CatalogAlias>(
            new CommandDefinition(
                """
                SELECT Fingerprint, CatalogId, Source, CreatedAt
                FROM   CatalogAlias
                WHERE  CatalogId = @CatalogId
                ORDER  BY CreatedAt;
                """,
                new { CatalogId = catalogId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<bool> PromoteAsync(
        string provisionalCatalogId,
        string assignedCatalogId,
        string source,
        string canonicalTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionalCatalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignedCatalogId);

        if (string.Equals(provisionalCatalogId, assignedCatalogId, StringComparison.Ordinal))
        {
            return true;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Rewriting the primary key is the whole operation: ON UPDATE CASCADE
            // carries every Game, AchievementDefinition, GameStatDefinition and
            // CatalogAlias reference across with it.
            //
            // Legitimate only because the old id is provisional. An assigned id is
            // immutable; see MergeIntoAsync for unifying two of those.
            var affected = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE CatalogEntry
                    SET    CatalogId      = @AssignedId,
                           Source         = @Source,
                           CanonicalTitle = @CanonicalTitle,
                           IsProvisional  = 0,
                           UpdatedAt      = @Now,
                           SyncedAt       = @Now
                    WHERE  CatalogId = @ProvisionalId;
                    """,
                    new
                    {
                        AssignedId = assignedCatalogId,
                        ProvisionalId = provisionalCatalogId,
                        Source = source,
                        CanonicalTitle = canonicalTitle,
                        Now = DateTimeOffset.Now
                    },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (affected > 0)
            {
                _logger.LogInformation(
                    "Promoted catalog entry {Provisional} to {Assigned} (source {Source}).",
                    provisionalCatalogId, assignedCatalogId, source);
            }

            return affected > 0;
        }
        catch (SqliteException ex)
            when (ex.SqliteExtendedErrorCode is SqliteConstraintPrimaryKey or SqliteConstraintUnique)
        {
            _logger.LogInformation(
                "Catalog entry {Assigned} already exists locally; {Provisional} needs merging.",
                assignedCatalogId, provisionalCatalogId);

            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> MergeIntoAsync(
        string sourceCatalogId,
        string targetCatalogId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCatalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCatalogId);

        if (string.Equals(sourceCatalogId, targetCatalogId, StringComparison.Ordinal))
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var parameters = new { Source = sourceCatalogId, Target = targetCatalogId };

            var sourceDefinitions = await ReadDefinitionsAsync(
                connection, transaction, sourceCatalogId, cancellationToken).ConfigureAwait(false);

            var targetDefinitions = (await ReadDefinitionsAsync(
                    connection, transaction, targetCatalogId, cancellationToken).ConfigureAwait(false))
                .ToDictionary(definition => definition.ApiName, StringComparer.OrdinalIgnoreCase);

            foreach (var source in sourceDefinitions)
            {
                if (!targetDefinitions.TryGetValue(source.ApiName, out var target))
                {
                    continue;
                }

                // Both entries define this achievement. Carry the user's history
                // onto the survivor before the duplicate goes, so a merge can never
                // cost somebody an unlock they earned.
                await CarryUnlockForwardAsync(
                    connection, transaction, source.Id, target.Id, cancellationToken).ConfigureAwait(false);

                await CarryProgressForwardAsync(
                    connection, transaction, source.Id, target.Id, cancellationToken).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM AchievementDefinition WHERE Id = @Id;",
                    new { source.Id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var repointed = 0;

            repointed += await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Game SET CatalogId = @Target WHERE CatalogId = @Source;",
                parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // Only the non-duplicate definitions remain to move.
            repointed += await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE AchievementDefinition SET CatalogId = @Target WHERE CatalogId = @Source;",
                parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            repointed += await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE GameStatDefinition SET CatalogId = @Target WHERE CatalogId = @Source;",
                parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // The absorbed entry's fingerprints now resolve to the survivor, which
            // is what makes the merge stick for future lookups.
            repointed += await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE OR IGNORE CatalogAlias SET CatalogId = @Target WHERE CatalogId = @Source;",
                parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // Kept, not deleted: a client or relay may still hold this identity,
            // and an assigned identity must never simply vanish.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE CatalogEntry
                SET    SupersededByCatalogId = @Target,
                       UpdatedAt             = @Now
                WHERE  CatalogId = @Source;
                """,
                new { Source = sourceCatalogId, Target = targetCatalogId, Now = DateTimeOffset.Now },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Merged catalog entry {Source} into {Target}; {Count} references repointed.",
                sourceCatalogId, targetCatalogId, repointed);

            return repointed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> DemoteForeignEntriesAsync(
        string currentRelaySource,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRelaySource);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Superseded entries are excluded: they are redirects with nothing
        // pointing at them, and rewriting one would break the chain it exists to
        // preserve.
        var foreign = await connection.QueryAsync<CatalogEntry>(new CommandDefinition(
            $"""
             {SelectColumns}
             WHERE  IsProvisional = 0
               AND  SupersededByCatalogId IS NULL
               AND  Source <> @Current
               AND  Source <> @Local;
             """,
            new { Current = currentRelaySource, Local = CatalogEntry.LocalSource },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var entries = foreign.AsList();
        if (entries.Count == 0)
        {
            return 0;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Rewriting the primary key is the whole operation, exactly as in
                // promotion. Every reference follows through ON UPDATE CASCADE, so
                // games, achievements, stats and aliases are untouched.
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE CatalogEntry
                    SET    CatalogId     = @NewId,
                           Source        = @Local,
                           IsProvisional = 1,
                           SyncedAt      = NULL,
                           UpdatedAt     = @Now
                    WHERE  CatalogId = @OldId;
                    """,
                    new
                    {
                        NewId = CatalogEntry.ProvisionalPrefix + Guid.NewGuid().ToString("N"),
                        OldId = entry.CatalogId,
                        Local = CatalogEntry.LocalSource,
                        Now = DateTimeOffset.Now
                    },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Demoted {Count} catalog entries assigned by another relay; they will be re-resolved.",
                entries.Count);

            return entries.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads the achievement definitions belonging to a catalog entry.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <param name="catalogId">Owning entry.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Identifier and api name for each definition.</returns>
    private static async Task<IReadOnlyList<DefinitionRow>> ReadDefinitionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string catalogId,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<DefinitionRow>(new CommandDefinition(
            "SELECT Id, ApiName FROM AchievementDefinition WHERE CatalogId = @CatalogId;",
            new { CatalogId = catalogId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>
    /// Gives the surviving definition the earlier of the two unlock times.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <param name="sourceDefinitionId">Definition being removed.</param>
    /// <param name="targetDefinitionId">Definition being kept.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <remarks>
    /// Compared as <see cref="DateTimeOffset"/> in memory rather than in SQL.
    /// Timestamps are stored as ISO-8601 text carrying their original offset, so
    /// two values written in different time zones do not order correctly under a
    /// lexicographic comparison.
    /// </remarks>
    private static async Task CarryUnlockForwardAsync(
        DbConnection connection,
        DbTransaction transaction,
        int sourceDefinitionId,
        int targetDefinitionId,
        CancellationToken cancellationToken)
    {
        var sourceUnlock = await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT UnlockedAt FROM AchievementUnlock WHERE DefinitionId = @Id;",
            new { Id = sourceDefinitionId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (sourceUnlock is null)
        {
            return;
        }

        var targetUnlock = await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT UnlockedAt FROM AchievementUnlock WHERE DefinitionId = @Id;",
            new { Id = targetDefinitionId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var effective = targetUnlock is null
            ? sourceUnlock.Value
            : (sourceUnlock.Value < targetUnlock.Value ? sourceUnlock.Value : targetUnlock.Value);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO AchievementUnlock (DefinitionId, UnlockedAt)
            VALUES (@Id, @UnlockedAt)
            ON CONFLICT (DefinitionId) DO UPDATE SET UnlockedAt = excluded.UnlockedAt;
            """,
            new { Id = targetDefinitionId, UnlockedAt = effective },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives the surviving definition the higher of the two progress values.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <param name="sourceDefinitionId">Definition being removed.</param>
    /// <param name="targetDefinitionId">Definition being kept.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <remarks>
    /// The maximum, because progress towards an achievement should never appear to
    /// go backwards as a result of administrative housekeeping.
    /// </remarks>
    private static async Task CarryProgressForwardAsync(
        DbConnection connection,
        DbTransaction transaction,
        int sourceDefinitionId,
        int targetDefinitionId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO AchievementProgress (DefinitionId, CurrentValue, UpdatedAt)
            SELECT @TargetId, s.CurrentValue, s.UpdatedAt
            FROM   AchievementProgress s
            WHERE  s.DefinitionId = @SourceId
            ON CONFLICT (DefinitionId) DO UPDATE
            SET    CurrentValue = MAX(excluded.CurrentValue, AchievementProgress.CurrentValue),
                   UpdatedAt    = excluded.UpdatedAt;
            """,
            new { SourceId = sourceDefinitionId, TargetId = targetDefinitionId },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Reads one entry using an existing connection.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="catalogId">Identity to read.</param>
    /// <param name="transaction">Enclosing transaction, if any.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    private static async Task<CatalogEntry?> GetByIdAsync(
        DbConnection connection,
        string catalogId,
        DbTransaction? transaction,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<CatalogEntry>(new CommandDefinition(
            $"{SelectColumns} WHERE CatalogId = @CatalogId;",
            new { CatalogId = catalogId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>Row shape for reading achievement definitions during a merge.</summary>
    private sealed class DefinitionRow
    {
        /// <summary>Definition identifier.</summary>
        public int Id { get; init; }

        /// <summary>Stable handle within its catalog entry.</summary>
        public string ApiName { get; init; } = string.Empty;
    }
}
