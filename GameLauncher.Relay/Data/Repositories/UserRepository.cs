using Dapper;

namespace GameLauncher.Relay.Data.Repositories;

/// <summary>Persistence for users.</summary>
public interface IUserRepository
{
    /// <summary>Gets a user by friend code.</summary>
    /// <param name="friendCode">The friend code to look up.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The user, or <see langword="null"/>.</returns>
    Task<RelayUser?> GetAsync(string friendCode, CancellationToken cancellationToken = default);

    /// <summary>Gets several users at once.</summary>
    /// <param name="friendCodes">Friend codes to fetch.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The users found, keyed by friend code.</returns>
    Task<IReadOnlyDictionary<string, RelayUser>> GetManyAsync(
        IReadOnlyCollection<string> friendCodes,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a user.</summary>
    /// <param name="user">The user to insert.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task AddAsync(RelayUser user, CancellationToken cancellationToken = default);

    /// <summary>Updates a user's display name.</summary>
    /// <param name="friendCode">The user to update.</param>
    /// <param name="displayName">The new display name.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was updated.</returns>
    Task<bool> UpdateDisplayNameAsync(
        string friendCode,
        string displayName,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistence for devices.</summary>
public interface IDeviceRepository
{
    /// <summary>
    /// Finds the active device holding a token hash.
    /// </summary>
    /// <param name="tokenHash">Hash of the presented token.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The device, or <see langword="null"/> when unknown or revoked.</returns>
    /// <remarks>The authentication hot path: one indexed read per request.</remarks>
    Task<RelayDevice?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Inserts a device.</summary>
    /// <param name="device">The device to insert.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task AddAsync(RelayDevice device, CancellationToken cancellationToken = default);

    /// <summary>Records that a device was just seen.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="seenAt">When it was seen.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task TouchAsync(string deviceId, DateTimeOffset seenAt, CancellationToken cancellationToken = default);
}

/// <summary>Dapper-backed <see cref="IUserRepository"/>.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly IRelayConnectionFactory _connectionFactory;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    public UserRepository(IRelayConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task<RelayUser?> GetAsync(string friendCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(friendCode))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RelayUser>(new CommandDefinition(
            "SELECT FriendCode, DisplayName, CreatedAt, UpdatedAt FROM AppUser WHERE FriendCode = @FriendCode;",
            new { FriendCode = friendCode }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, RelayUser>> GetManyAsync(
        IReadOnlyCollection<string> friendCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendCodes);

        if (friendCodes.Count == 0)
        {
            return new Dictionary<string, RelayUser>(StringComparer.Ordinal);
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<RelayUser>(new CommandDefinition(
            "SELECT FriendCode, DisplayName, CreatedAt, UpdatedAt FROM AppUser WHERE FriendCode IN @Codes;",
            new { Codes = friendCodes }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToDictionary(user => user.FriendCode, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task AddAsync(RelayUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO AppUser (FriendCode, DisplayName, CreatedAt, UpdatedAt)
            VALUES (@FriendCode, @DisplayName, @CreatedAt, @UpdatedAt);
            """,
            user, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateDisplayNameAsync(
        string friendCode,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE AppUser SET DisplayName = @DisplayName, UpdatedAt = @UpdatedAt WHERE FriendCode = @FriendCode;",
            new { FriendCode = friendCode, DisplayName = displayName, UpdatedAt = DateTimeOffset.UtcNow },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }
}

/// <summary>Dapper-backed <see cref="IDeviceRepository"/>.</summary>
public sealed class DeviceRepository : IDeviceRepository
{
    private readonly IRelayConnectionFactory _connectionFactory;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="connectionFactory">Supplies database connections.</param>
    public DeviceRepository(IRelayConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    /// <inheritdoc />
    public async Task<RelayDevice?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Revoked devices are excluded here rather than by the caller, so no code
        // path can accidentally authenticate one.
        return await connection.QuerySingleOrDefaultAsync<RelayDevice>(new CommandDefinition(
            """
            SELECT DeviceId, FriendCode, TokenHash, Label, CreatedAt, LastSeenAt, RevokedAt
            FROM   Device
            WHERE  TokenHash = @TokenHash AND RevokedAt IS NULL;
            """,
            new { TokenHash = tokenHash }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(RelayDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Device (DeviceId, FriendCode, TokenHash, Label, CreatedAt, LastSeenAt, RevokedAt)
            VALUES (@DeviceId, @FriendCode, @TokenHash, @Label, @CreatedAt, @LastSeenAt, @RevokedAt);
            """,
            device, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TouchAsync(
        string deviceId,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Device SET LastSeenAt = @SeenAt WHERE DeviceId = @DeviceId;",
            new { DeviceId = deviceId, SeenAt = seenAt },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
