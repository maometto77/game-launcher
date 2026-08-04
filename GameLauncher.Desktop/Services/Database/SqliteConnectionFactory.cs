using System.Data.Common;
using GameLauncher.Desktop.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Opens SQLite connections against the file described by <see cref="IAppPaths"/>.
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteConnectionFactory> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="paths">Supplies the database file location.</param>
    /// <param name="logger">Logger for connection diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SqliteConnectionFactory(IAppPaths paths, ILogger<SqliteConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Serialised threading lets the pooled connection be used safely from
            // whichever thread a repository call happens to run on.
            Cache = SqliteCacheMode.Default,
            Pooling = true,

            // Wait rather than fail immediately when another connection holds the
            // write lock. Contention here is brief — a metadata write while the
            // achievement poller reads — so a short wait is far better than
            // surfacing "database is locked" to the user.
            DefaultTimeout = 15
        }.ToString();

        _logger.LogDebug("SQLite database file: {Path}", paths.DatabaseFile);
    }

    /// <inheritdoc />
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Applies the per-connection pragmas the schema depends on.
    /// </summary>
    /// <param name="connection">The freshly opened connection.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <remarks>
    /// <c>foreign_keys</c> is off by default in SQLite and is per-connection, so
    /// it has to be set every time or the schema's cascade rules silently do
    /// nothing. <c>journal_mode=WAL</c> lets the achievement poller read while a
    /// playtime update writes, instead of the two blocking each other.
    /// </remarks>
    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
