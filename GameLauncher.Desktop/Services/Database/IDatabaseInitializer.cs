namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Brings the local database up to the schema version this build expects.
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Creates the database if absent and applies any outstanding migrations.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    /// <exception cref="InvalidOperationException">
    /// The database was created by a newer build and cannot be downgraded.
    /// </exception>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
