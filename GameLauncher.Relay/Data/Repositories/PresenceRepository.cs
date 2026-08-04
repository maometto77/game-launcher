using Dapper;

namespace GameLauncher.Relay.Data.Repositories;

/// <summary>Persistence for presence.</summary>
public interface IPresenceRepository
{
    /// <summary>Gets one user's presence.</summary>
    /// <param name="friendCode">The user.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The presence, or <see langword="null"/> when never recorded.</returns>
    Task<RelayPresence?> GetAsync(string friendCode, CancellationToken cancellationToken = default);

    /// <summary>Gets presence for several users.</summary>
    /// <param name="friendCodes">The users.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Presence keyed by friend code, omitting users with none recorded.</returns>
    Task<IReadOnlyDictionary<string, RelayPresence>> GetManyAsync(
        IReadOnlyCollection<string> friendCodes,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a user's presence.</summary>
    /// <param name="presence">The presence to store.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task UpsertAsync(RelayPresence presence, CancellationToken cancellationToken = default);

    /// <summary>Marks a user offline and stamps when they were last seen.</summary>
    /// <param name="friendCode">The user.</param>
    /// <param name="lastSeenAt">When they disconnected.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    /// <remarks>
    /// Clears the current game as well as the online flag. A user who is offline
    /// is not playing anything, and leaving a stale title behind would show
    /// friends a game that ended hours ago.
    /// </remarks>
    Task SetOfflineAsync(
        string friendCode,
        DateTimeOffset lastSeenAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper-backed <see cref="IPresenceRepository"/>.</summary>
public sealed class PresenceRepository : IPresenceRepository
{
    private const string SelectColumns = """
        SELECT FriendCode, CurrentGameTitle, CurrentGameCatalogId, IsOnline, LastSeenAt
        FROM   Presence
        """;

    private readonly IRelayConnectionFactory _connectionFactory;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    public PresenceRepository(IRelayConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task<RelayPresence?> GetAsync(
        string friendCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RelayPresence>(new CommandDefinition(
            $"{SelectColumns} WHERE FriendCode = @FriendCode;",
            new { FriendCode = friendCode }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, RelayPresence>> GetManyAsync(
        IReadOnlyCollection<string> friendCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendCodes);

        if (friendCodes.Count == 0)
        {
            return new Dictionary<string, RelayPresence>(StringComparer.Ordinal);
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<RelayPresence>(new CommandDefinition(
            $"{SelectColumns} WHERE FriendCode IN @Codes;",
            new { Codes = friendCodes }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToDictionary(presence => presence.FriendCode, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(RelayPresence presence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presence);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // ON CONFLICT ... DO UPDATE rather than SQLite's INSERT OR REPLACE, which
        // PostgreSQL does not have. This form is standard in both.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Presence (FriendCode, CurrentGameTitle, CurrentGameCatalogId, IsOnline, LastSeenAt)
            VALUES (@FriendCode, @CurrentGameTitle, @CurrentGameCatalogId, @IsOnline, @LastSeenAt)
            ON CONFLICT (FriendCode) DO UPDATE SET
                CurrentGameTitle     = excluded.CurrentGameTitle,
                CurrentGameCatalogId = excluded.CurrentGameCatalogId,
                IsOnline             = excluded.IsOnline,
                LastSeenAt           = excluded.LastSeenAt;
            """,
            presence, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetOfflineAsync(
        string friendCode,
        DateTimeOffset lastSeenAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Presence (FriendCode, CurrentGameTitle, CurrentGameCatalogId, IsOnline, LastSeenAt)
            VALUES (@FriendCode, NULL, NULL, @Offline, @LastSeenAt)
            ON CONFLICT (FriendCode) DO UPDATE SET
                CurrentGameTitle     = NULL,
                CurrentGameCatalogId = NULL,
                IsOnline             = @Offline,
                LastSeenAt           = excluded.LastSeenAt;
            """,
            new { FriendCode = friendCode, LastSeenAt = lastSeenAt, Offline = false },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
