using Dapper;
using GameLauncher.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Dapper-backed <see cref="IFriendCacheRepository"/>.
/// </summary>
public sealed class FriendCacheRepository : IFriendCacheRepository
{
    private const string UpsertSql = """
        INSERT INTO FriendCache (FriendCode, DisplayName, LastKnownGame, LastSeenAt, AvatarPath)
        VALUES (@FriendCode, @DisplayName, @LastKnownGame, @LastSeenAt, @AvatarPath)
        ON CONFLICT (FriendCode) DO UPDATE SET
            DisplayName   = excluded.DisplayName,
            LastKnownGame = excluded.LastKnownGame,
            LastSeenAt    = excluded.LastSeenAt,
            AvatarPath    = excluded.AvatarPath;
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<FriendCacheRepository> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    /// <param name="logger">Logger for persistence diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public FriendCacheRepository(IDbConnectionFactory connectionFactory, ILogger<FriendCacheRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FriendCache>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<FriendCache>(
            new CommandDefinition(
                """
                SELECT FriendCode, DisplayName, LastKnownGame, LastSeenAt, AvatarPath
                FROM   FriendCache
                ORDER  BY DisplayName COLLATE NOCASE;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(FriendCache friend, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friend);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                UpsertSql,
                new
                {
                    friend.FriendCode,
                    friend.DisplayName,
                    friend.LastKnownGame,
                    friend.LastSeenAt,
                    friend.AvatarPath
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReplaceAllAsync(
        IReadOnlyCollection<FriendCache> friends,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friends);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM FriendCache;",
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (friends.Count > 0)
            {
                // Dapper executes the statement once per element of the sequence.
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        UpsertSql,
                        friends.Select(friend => new
                        {
                            friend.FriendCode,
                            friend.DisplayName,
                            friend.LastKnownGame,
                            friend.LastSeenAt,
                            friend.AvatarPath
                        }).ToArray(),
                        transaction: transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Friend cache replaced with {Count} entries.", friends.Count);
        }
        catch
        {
            // Rolled back with an uncancellable token: the delete has already run,
            // and abandoning it would leave the cache empty rather than stale.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string friendCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(friendCode))
        {
            return false;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM FriendCache WHERE FriendCode = @FriendCode;",
                new { FriendCode = friendCode },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }
}
