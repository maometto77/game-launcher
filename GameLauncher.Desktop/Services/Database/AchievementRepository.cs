using Dapper;
using GameLauncher.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Dapper-backed <see cref="IAchievementRepository"/>.
/// </summary>
public sealed class AchievementRepository : IAchievementRepository
{
    private const string SelectDefinitionColumns = """
        SELECT Id, CatalogId, ApiName, GlobalKey, Title, Description, IconPath, Kind, ProviderKey,
               TriggerConfigJson, IsHidden, SortOrder, ProgressTarget, StatApiName, UpdatedAt
        FROM   AchievementDefinition
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<AchievementRepository> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <param name="logger">Logger for persistence diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AchievementRepository(IDbConnectionFactory connectionFactory, ILogger<AchievementRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AchievementDefinition>> GetAllDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AchievementDefinition>(
            new CommandDefinition(
                $"{SelectDefinitionColumns} ORDER BY CatalogId, SortOrder, Title COLLATE NOCASE;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AchievementDefinition>> GetDefinitionsForCatalogAsync(
        string catalogId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AchievementDefinition>(
            new CommandDefinition(
                $"{SelectDefinitionColumns} WHERE CatalogId = @CatalogId ORDER BY SortOrder, Title COLLATE NOCASE;",
                new { CatalogId = catalogId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AchievementDefinition>> GetLibraryWideDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AchievementDefinition>(
            new CommandDefinition(
                $"{SelectDefinitionColumns} WHERE CatalogId IS NULL ORDER BY SortOrder, Title COLLATE NOCASE;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<AchievementDefinition?> GetDefinitionByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<AchievementDefinition>(
            new CommandDefinition(
                $"{SelectDefinitionColumns} WHERE Id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> AddDefinitionAsync(
        AchievementDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(definition.GlobalKey))
        {
            definition.GlobalKey = GameRepository.NewGlobalKey();
        }

        // A definition with no explicit handle still needs a stable one, because
        // the unique index over (game, api name) will not accept a second blank.
        if (string.IsNullOrWhiteSpace(definition.ApiName))
        {
            definition.ApiName = $"ACH_{definition.GlobalKey[..8].ToUpperInvariant()}";
        }

        definition.UpdatedAt = DateTimeOffset.Now;

        var id = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO AchievementDefinition
                    (CatalogId, ApiName, GlobalKey, Title, Description, IconPath, Kind, ProviderKey,
                     TriggerConfigJson, IsHidden, SortOrder, ProgressTarget, StatApiName, UpdatedAt)
                VALUES
                    (@CatalogId, @ApiName, @GlobalKey, @Title, @Description, @IconPath, @Kind, @ProviderKey,
                     @TriggerConfigJson, @IsHidden, @SortOrder, @ProgressTarget, @StatApiName, @UpdatedAt);

                SELECT last_insert_rowid();
                """,
                new
                {
                    definition.CatalogId,
                    definition.ApiName,
                    definition.ProviderKey,
                    definition.GlobalKey,
                    definition.Title,
                    definition.Description,
                    definition.IconPath,
                    Kind = (int)definition.Kind,
                    definition.TriggerConfigJson,
                    definition.IsHidden,
                    definition.SortOrder,
                    definition.ProgressTarget,
                    definition.StatApiName,
                    definition.UpdatedAt
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        definition.Id = (int)id;
        _logger.LogInformation(
            "Added {Kind} achievement {Title} (id {Id}).", definition.Kind, definition.Title, definition.Id);

        return definition.Id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateDefinitionAsync(
        AchievementDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        definition.UpdatedAt = DateTimeOffset.Now;

        // GlobalKey is identity and is never rewritten; see AchievementDefinition.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE AchievementDefinition
                SET    CatalogId         = @CatalogId,
                       ApiName           = @ApiName,
                       Title             = @Title,
                       Description       = @Description,
                       IconPath          = @IconPath,
                       Kind              = @Kind,
                       ProviderKey       = @ProviderKey,
                       TriggerConfigJson = @TriggerConfigJson,
                       IsHidden          = @IsHidden,
                       SortOrder         = @SortOrder,
                       ProgressTarget    = @ProgressTarget,
                       StatApiName       = @StatApiName,
                       UpdatedAt         = @UpdatedAt
                WHERE  Id = @Id;
                """,
                new
                {
                    definition.CatalogId,
                    definition.ApiName,
                    definition.Title,
                    definition.Description,
                    definition.IconPath,
                    Kind = (int)definition.Kind,
                    definition.ProviderKey,
                    definition.TriggerConfigJson,
                    definition.IsHidden,
                    definition.SortOrder,
                    definition.ProgressTarget,
                    definition.StatApiName,
                    definition.UpdatedAt,
                    definition.Id
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDefinitionAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // AchievementUnlock cascades from the definition.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM AchievementDefinition WHERE Id = @Id;",
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AchievementUnlock>> GetUnlocksAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AchievementUnlock>(
            new CommandDefinition(
                "SELECT DefinitionId, UnlockedAt, SyncedAt FROM AchievementUnlock ORDER BY UnlockedAt DESC;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> GetUnlockedDefinitionIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var ids = await connection.QueryAsync<int>(
            new CommandDefinition(
                "SELECT DefinitionId FROM AchievementUnlock;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return ids.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<bool> UnlockAsync(
        int definitionId,
        DateTimeOffset unlockedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // INSERT OR IGNORE against the primary key makes this atomically
        // idempotent: concurrent evaluators cannot both report a fresh unlock,
        // so the toast fires exactly once.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT OR IGNORE INTO AchievementUnlock (DefinitionId, UnlockedAt)
                VALUES (@DefinitionId, @UnlockedAt);
                """,
                new { DefinitionId = definitionId, UnlockedAt = unlockedAt },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected > 0)
        {
            _logger.LogInformation("Unlocked achievement definition {DefinitionId}.", definitionId);
        }

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingUnlock>> GetUnsyncedUnlocksAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Provisional entries are filtered out here rather than by the caller: the
        // relay has never heard of a 'local:' identity, so sending one would be
        // rejected on every attempt and block the queue behind it.
        var rows = await connection.QueryAsync<PendingUnlock>(
            new CommandDefinition(
                """
                SELECT u.DefinitionId, d.CatalogId, d.ApiName, u.UnlockedAt
                FROM   AchievementUnlock u
                JOIN   AchievementDefinition d ON d.Id = u.DefinitionId
                JOIN   CatalogEntry c          ON c.CatalogId = d.CatalogId
                WHERE  u.SyncedAt IS NULL
                  AND  c.IsProvisional = 0
                ORDER  BY u.UnlockedAt
                LIMIT  @Limit;
                """,
                new { Limit = limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<int> MarkUnlocksSyncedAsync(
        IReadOnlyCollection<int> definitionIds,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionIds);

        if (definitionIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE AchievementUnlock SET SyncedAt = @SyncedAt WHERE DefinitionId IN @Ids;",
                new { SyncedAt = syncedAt, Ids = definitionIds },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, AchievementProgress>> GetProgressAsync(
        IReadOnlyCollection<int> definitionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionIds);

        if (definitionIds.Count == 0)
        {
            return new Dictionary<int, AchievementProgress>();
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AchievementProgress>(
            new CommandDefinition(
                """
                SELECT DefinitionId, CurrentValue, UpdatedAt
                FROM   AchievementProgress
                WHERE  DefinitionId IN @Ids;
                """,
                new { Ids = definitionIds },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToDictionary(progress => progress.DefinitionId);
    }

    /// <inheritdoc />
    public async Task<bool> RecordProgressAsync(
        int definitionId,
        double currentValue,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The WHERE on the DO UPDATE is what makes progress monotonic: a lower
        // observation is discarded rather than written, so the row is only touched
        // when it genuinely advanced.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO AchievementProgress (DefinitionId, CurrentValue, UpdatedAt)
                VALUES (@DefinitionId, @CurrentValue, @UpdatedAt)
                ON CONFLICT (DefinitionId) DO UPDATE
                SET CurrentValue = excluded.CurrentValue,
                    UpdatedAt    = excluded.UpdatedAt
                WHERE excluded.CurrentValue > AchievementProgress.CurrentValue;
                """,
                new { DefinitionId = definitionId, CurrentValue = currentValue, UpdatedAt = updatedAt },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<int> ResetUnlockSyncStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE AchievementUnlock SET SyncedAt = NULL WHERE SyncedAt IS NOT NULL;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetUnlockCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM AchievementUnlock;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
