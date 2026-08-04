using Dapper;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Dapper-backed <see cref="IPlaySessionRepository"/>.
/// </summary>
public sealed class PlaySessionRepository : IPlaySessionRepository
{
    private const string SelectColumns = """
        SELECT Id, SessionKey, GameId, DeviceId, StartedAt, EndedAt, DurationSeconds, SyncedAt
        FROM   PlaySession
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connectionFactory"/> is <see langword="null"/>.</exception>
    public PlaySessionRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <inheritdoc />
    public async Task<int> StartAsync(
        int gameId,
        DateTimeOffset startedAt,
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var id = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO PlaySession (SessionKey, GameId, DeviceId, StartedAt, EndedAt, DurationSeconds, SyncedAt)
                VALUES (@SessionKey, @GameId, @DeviceId, @StartedAt, NULL, NULL, NULL);

                SELECT last_insert_rowid();
                """,
                new
                {
                    // Assigned at the start rather than at push time, so the
                    // identity exists before anything can go wrong and a session
                    // interrupted by a crash still has one.
                    SessionKey = Guid.NewGuid().ToString("N"),
                    GameId = gameId,
                    DeviceId = deviceId,
                    StartedAt = startedAt
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (int)id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlaySession>> GetUnsyncedAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Completed sessions only. A session still in progress has no duration to
        // report, and pushing it would mean pushing it again when it ends.
        var rows = await connection.QueryAsync<PlaySession>(
            new CommandDefinition(
                $"""
                 {SelectColumns}
                 WHERE  SyncedAt IS NULL AND EndedAt IS NOT NULL
                 ORDER  BY StartedAt
                 LIMIT  @Limit;
                 """,
                new { Limit = limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<int> MarkSyncedAsync(
        IReadOnlyCollection<string> sessionKeys,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionKeys);

        if (sessionKeys.Count == 0)
        {
            return 0;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE PlaySession SET SyncedAt = @SyncedAt WHERE SessionKey IN @Keys;",
                new { SyncedAt = syncedAt, Keys = sessionKeys },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(
        int sessionId,
        DateTimeOffset endedAt,
        long durationSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationSeconds);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The EndedAt IS NULL guard makes completion idempotent: a session
        // reconciled at startup cannot later be closed a second time by a
        // process-exit handler that fires late.
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE PlaySession
                SET    EndedAt         = @EndedAt,
                       DurationSeconds = @DurationSeconds
                WHERE  Id = @Id AND EndedAt IS NULL;
                """,
                new { Id = sessionId, EndedAt = endedAt, DurationSeconds = durationSeconds },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<int> ResetSyncStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE PlaySession SET SyncedAt = NULL WHERE SyncedAt IS NOT NULL;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlaySession>> GetInProgressAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<PlaySession>(
            new CommandDefinition(
                $"{SelectColumns} WHERE EndedAt IS NULL ORDER BY StartedAt;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlaySession>> GetRecentForGameAsync(
        int gameId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<PlaySession>(
            new CommandDefinition(
                $"{SelectColumns} WHERE GameId = @GameId ORDER BY StartedAt DESC LIMIT @Limit;",
                new { GameId = gameId, Limit = limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<int> CountForGameAsync(int gameId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM PlaySession WHERE GameId = @GameId AND EndedAt IS NOT NULL;",
                new { GameId = gameId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
