using Dapper;

namespace GameLauncher.Relay.Data.Repositories;

/// <summary>Persistence for synchronised achievement history.</summary>
public interface IAchievementRepository
{
    /// <summary>Gets a user's unlocks for a set of titles.</summary>
    /// <param name="friendCode">The user.</param>
    /// <param name="catalogIds">The titles of interest.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Every unlock the relay holds for those titles.</returns>
    Task<IReadOnlyList<RelayUserAchievement>> GetForUserAsync(
        string friendCode,
        IReadOnlyCollection<string> catalogIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges pushed unlocks into the relay, keeping the earliest time for each.
    /// </summary>
    /// <param name="friendCode">The user the unlocks belong to.</param>
    /// <param name="unlocks">The unlocks being pushed.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>How many unlocks were new or moved the recorded time earlier.</returns>
    /// <remarks>
    /// Idempotent, which is what makes retrying a batch whose response was lost
    /// safe. Earliest-wins means a replay can never move an earned-on date
    /// forward, and a second device reporting the same achievement later cannot
    /// overwrite the first device's earlier record.
    /// </remarks>
    Task<int> MergeAsync(
        string friendCode,
        IReadOnlyCollection<RelayUserAchievement> unlocks,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper-backed <see cref="IAchievementRepository"/>.</summary>
public sealed class AchievementRepository : IAchievementRepository
{
    private readonly IRelayConnectionFactory _connectionFactory;
    private readonly ILogger<AchievementRepository> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <param name="logger">Logger for sync diagnostics.</param>
    public AchievementRepository(
        IRelayConnectionFactory connectionFactory,
        ILogger<AchievementRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelayUserAchievement>> GetForUserAsync(
        string friendCode,
        IReadOnlyCollection<string> catalogIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogIds);

        if (catalogIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<RelayUserAchievement>(new CommandDefinition(
            """
            SELECT FriendCode, CatalogId, ApiName, UnlockedAt
            FROM   UserAchievement
            WHERE  FriendCode = @FriendCode AND CatalogId IN @CatalogIds;
            """,
            new { FriendCode = friendCode, CatalogIds = catalogIds },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<int> MergeAsync(
        string friendCode,
        IReadOnlyCollection<RelayUserAchievement> unlocks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unlocks);

        if (unlocks.Count == 0)
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var accepted = 0;

            foreach (var unlock in unlocks)
            {
                // The CASE keeps the earlier of the two times. Written as CASE
                // rather than MIN() because SQLite's two-argument MIN has no
                // PostgreSQL equivalent — there it is LEAST — whereas CASE is
                // identical on both.
                //
                // The comparison is a text comparison, which is correct precisely
                // because timestamps are stored as fixed-width UTC ISO-8601: that
                // format sorts lexicographically in chronological order.
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO UserAchievement (FriendCode, CatalogId, ApiName, UnlockedAt)
                    VALUES (@FriendCode, @CatalogId, @ApiName, @UnlockedAt)
                    ON CONFLICT (FriendCode, CatalogId, ApiName) DO UPDATE
                    SET UnlockedAt = CASE
                            WHEN excluded.UnlockedAt < UserAchievement.UnlockedAt
                            THEN excluded.UnlockedAt
                            ELSE UserAchievement.UnlockedAt
                        END
                    WHERE excluded.UnlockedAt < UserAchievement.UnlockedAt;
                    """,
                    new
                    {
                        FriendCode = friendCode,
                        unlock.CatalogId,
                        unlock.ApiName,
                        unlock.UnlockedAt
                    },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

                // The WHERE on the DO UPDATE means a row is only touched when it
                // was inserted or genuinely improved, so this is an accurate count
                // of what changed rather than of what was sent.
                accepted += affected;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Merged {Accepted} of {Total} pushed unlocks for {FriendCode}.",
                accepted, unlocks.Count, friendCode);

            return accepted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
