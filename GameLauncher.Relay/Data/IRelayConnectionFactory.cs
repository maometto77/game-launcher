using System.Data.Common;
using GameLauncher.Relay.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GameLauncher.Relay.Data;

/// <summary>
/// Opens connections to the relay's database.
/// </summary>
/// <remarks>
/// The single seam between the relay and its database engine. Every query in the
/// service is written in portable SQL, so moving to PostgreSQL means adding one
/// implementation of this interface and changing configuration — nothing else.
/// </remarks>
public interface IRelayConnectionFactory
{
    /// <summary>Gets the engine this factory connects to.</summary>
    RelayDatabaseProvider Provider { get; }

    /// <summary>
    /// Opens a new connection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>An open connection the caller owns and must dispose.</returns>
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite <see cref="IRelayConnectionFactory"/>.
/// </summary>
public sealed class SqliteRelayConnectionFactory : IRelayConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="options">Relay configuration supplying the connection string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public SqliteRelayConnectionFactory(IOptions<RelayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.Database.ConnectionString;
    }

    /// <inheritdoc />
    public RelayDatabaseProvider Provider => RelayDatabaseProvider.Sqlite;

    /// <inheritdoc />
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();

            // Per-connection and off by default in SQLite, so it has to be set
            // every time or the schema's cascade rules quietly do nothing.
            // PostgreSQL enforces foreign keys unconditionally, which is why this
            // lives in the SQLite factory rather than in the shared schema.
            command.CommandText =
                """
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 10000;
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
