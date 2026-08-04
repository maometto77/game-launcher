using System.Data.Common;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Creates open connections to the launcher's local database.
/// </summary>
/// <remarks>
/// Repositories take a factory rather than a shared connection. SQLite
/// connections are cheap to open (the underlying file handle is pooled by
/// Microsoft.Data.Sqlite) and a connection per unit of work removes any question
/// of two operations sharing transaction state across threads.
/// </remarks>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Opens a new connection with the launcher's pragmas already applied.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>An open connection the caller owns and must dispose.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database could not be opened.</exception>
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}
