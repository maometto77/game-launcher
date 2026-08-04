using Dapper;
using GameLauncher.Shared.Enums;

namespace GameLauncher.Relay.Data.Repositories;

/// <summary>Persistence for friendships and requests.</summary>
public interface IFriendshipRepository
{
    /// <summary>Gets every friendship involving a user, in either direction.</summary>
    /// <param name="friendCode">The user.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>All relationships the user is party to.</returns>
    Task<IReadOnlyList<RelayFriendship>> GetForUserAsync(
        string friendCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the relationship between two users, whichever way round it was
    /// created.
    /// </summary>
    /// <param name="first">One party.</param>
    /// <param name="second">The other party.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The relationship, or <see langword="null"/>.</returns>
    Task<RelayFriendship?> FindBetweenAsync(
        string first,
        string second,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the friend codes of a user's accepted friends.</summary>
    /// <param name="friendCode">The user.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Accepted friends, in either direction.</returns>
    /// <remarks>The presence fan-out list: only accepted friends ever see presence.</remarks>
    Task<IReadOnlyList<string>> GetAcceptedFriendCodesAsync(
        string friendCode,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a pending request.</summary>
    /// <param name="friendship">The request to store.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task AddAsync(RelayFriendship friendship, CancellationToken cancellationToken = default);

    /// <summary>Accepts a pending request.</summary>
    /// <param name="requester">The user who sent it.</param>
    /// <param name="addressee">The user who received it.</param>
    /// <param name="respondedAt">When it was accepted.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a pending row was accepted.</returns>
    Task<bool> AcceptAsync(
        string requester,
        string addressee,
        DateTimeOffset respondedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a relationship, whichever way round it was created.</summary>
    /// <param name="first">One party.</param>
    /// <param name="second">The other party.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was removed.</returns>
    /// <remarks>
    /// Used both for rejecting a request and for removing a friend. A rejection
    /// deletes rather than recording a refusal, so the requester can try again
    /// later and so the relay does not keep a list of who declined whom.
    /// </remarks>
    Task<bool> RemoveAsync(string first, string second, CancellationToken cancellationToken = default);
}

/// <summary>Dapper-backed <see cref="IFriendshipRepository"/>.</summary>
public sealed class FriendshipRepository : IFriendshipRepository
{
    private const string SelectColumns = """
        SELECT UserFriendCode, FriendFriendCode, Status, CreatedAt, RespondedAt
        FROM   Friendship
        """;

    private readonly IRelayConnectionFactory _connectionFactory;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    public FriendshipRepository(IRelayConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelayFriendship>> GetForUserAsync(
        string friendCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<RelayFriendship>(new CommandDefinition(
            $"{SelectColumns} WHERE UserFriendCode = @Code OR FriendFriendCode = @Code;",
            new { Code = friendCode }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<RelayFriendship?> FindBetweenAsync(
        string first,
        string second,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Either direction: the row belongs to whoever sent the request, and the
        // caller may be on either side of it.
        return await connection.QueryFirstOrDefaultAsync<RelayFriendship>(new CommandDefinition(
            $"""
             {SelectColumns}
             WHERE (UserFriendCode = @First AND FriendFriendCode = @Second)
                OR (UserFriendCode = @Second AND FriendFriendCode = @First);
             """,
            new { First = first, Second = second },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAcceptedFriendCodesAsync(
        string friendCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT CASE WHEN UserFriendCode = @Code THEN FriendFriendCode ELSE UserFriendCode END
            FROM   Friendship
            WHERE  Status = @Accepted
              AND  (UserFriendCode = @Code OR FriendFriendCode = @Code);
            """,
            new { Code = friendCode, Accepted = (int)FriendshipStatus.Accepted },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task AddAsync(RelayFriendship friendship, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendship);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Friendship (UserFriendCode, FriendFriendCode, Status, CreatedAt, RespondedAt)
            VALUES (@UserFriendCode, @FriendFriendCode, @Status, @CreatedAt, @RespondedAt);
            """,
            new
            {
                friendship.UserFriendCode,
                friendship.FriendFriendCode,
                Status = (int)friendship.Status,
                friendship.CreatedAt,
                friendship.RespondedAt
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AcceptAsync(
        string requester,
        string addressee,
        DateTimeOffset respondedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The direction is fixed and the current status is checked in the WHERE
        // clause, so this cannot accept a request the caller sent themselves, nor
        // re-accept one that is already accepted.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Friendship
            SET    Status = @Accepted, RespondedAt = @RespondedAt
            WHERE  UserFriendCode = @Requester
              AND  FriendFriendCode = @Addressee
              AND  Status = @Pending;
            """,
            new
            {
                Requester = requester,
                Addressee = addressee,
                RespondedAt = respondedAt,
                Accepted = (int)FriendshipStatus.Accepted,
                Pending = (int)FriendshipStatus.Pending
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        string first,
        string second,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM Friendship
            WHERE (UserFriendCode = @First AND FriendFriendCode = @Second)
               OR (UserFriendCode = @Second AND FriendFriendCode = @First);
            """,
            new { First = first, Second = second },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }
}
