namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Populates an empty library with representative sample data.
/// </summary>
/// <remarks>
/// <para>
/// Development aid, used to exercise the library, collection and achievement UI
/// without needing real games installed. It is deliberately opt-in: seeding runs
/// only when the process is started with <c>--seed-sample-data</c>.
/// </para>
/// <para>
/// Seeding into a library that already holds games is refused outright. Sample
/// entries point at executables that do not exist, and mixing them into somebody's
/// real library would leave them hand-deleting rows to clean up.
/// </para>
/// </remarks>
public interface ISampleDataSeeder
{
    /// <summary>
    /// Seeds sample games, collections and achievements if the library is empty.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when sample data was written;
    /// <see langword="false"/> when the library already had content and was left alone.
    /// </returns>
    Task<bool> SeedAsync(CancellationToken cancellationToken = default);
}
