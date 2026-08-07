using System.Globalization;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Launcher;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Migrates the local database as part of host startup.
/// </summary>
/// <remarks>
/// Runs as a hosted service so that <c>StartAsync</c> on the host does not
/// complete until the schema is current. The shell window is shown after that
/// point, which guarantees no page can query a table that does not exist yet.
/// </remarks>
public sealed class DatabaseStartupService : IHostedService
{
    private readonly IDatabaseInitializer _initializer;
    private readonly ISampleDataSeeder _seeder;
    private readonly IGameLaunchService _launcher;
    private readonly ICatalogService _catalog;
    private readonly IAppPaths _paths;
    private readonly IStartupNotices _notices;
    private readonly StartupOptions _options;
    private readonly ILogger<DatabaseStartupService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="initializer">Performs the schema migration.</param>
    /// <param name="seeder">Populates sample data when requested.</param>
    /// <param name="launcher">Used to close out sessions left open by a previous run.</param>
    /// <param name="catalog">Used to repair catalog entries left without a fingerprint.</param>
    /// <param name="paths">Resolves the database file, for recovery.</param>
    /// <param name="notices">Carries a recovery message through to the shell.</param>
    /// <param name="options">Parsed command-line options.</param>
    /// <param name="logger">Logger for startup diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DatabaseStartupService(
        IDatabaseInitializer initializer,
        ISampleDataSeeder seeder,
        IGameLaunchService launcher,
        ICatalogService catalog,
        IAppPaths paths,
        IStartupNotices notices,
        StartupOptions options,
        ILogger<DatabaseStartupService> logger)
    {
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _seeder = seeder ?? throw new ArgumentNullException(nameof(seeder));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Preparing the local database.");

        try
        {
            await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            // A damaged file previously meant the launcher simply refused to
            // start, showing a raw SQLite message and offering the user nothing
            // to do about it. The file is preserved rather than deleted — it is
            // the user's data, however unreadable, and it is not ours to discard.
            await RecoverFromCorruptionAsync(ex, cancellationToken).ConfigureAwait(false);
        }

        // Must run before any page can read playtime, so the library never shows
        // a session that is still "in progress" from a run that already ended.
        await _launcher.ReconcileInterruptedSessionsAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent and a no-op once there is nothing to fix. Repairs catalog
        // entries created by the v3 SQL backfill, which could not compute a
        // fingerprint and would otherwise never match on re-add.
        await _catalog.RepairMissingFingerprintsAsync(cancellationToken).ConfigureAwait(false);

        if (_options.SeedSampleData)
        {
            _logger.LogInformation("Sample data was requested on the command line.");
            await _seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Determines whether a SQLite failure means the file itself is damaged.
    /// </summary>
    /// <param name="exception">The failure to classify.</param>
    /// <returns><see langword="true"/> when the database cannot be read at all.</returns>
    /// <remarks>
    /// Deliberately narrow. Only <c>SQLITE_CORRUPT</c> and <c>SQLITE_NOTADB</c>
    /// mean the file is unusable; a locked or busy database is a transient
    /// condition and moving it aside would destroy a perfectly good library.
    /// </remarks>
    private static bool IsCorruption(SqliteException exception) =>
        exception.SqliteErrorCode is 11 or 26;

    /// <summary>
    /// Moves a damaged database aside and builds a fresh one in its place.
    /// </summary>
    /// <param name="failure">The corruption that triggered recovery.</param>
    /// <param name="cancellationToken">Cancels the rebuild.</param>
    /// <exception cref="InvalidOperationException">The damaged file could not be moved.</exception>
    private async Task RecoverFromCorruptionAsync(SqliteException failure, CancellationToken cancellationToken)
    {
        var databaseFile = _paths.DatabaseFile;
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var preserved = $"{databaseFile}.corrupt-{stamp}";

        _logger.LogError(
            failure, "The library database at {Path} is damaged and cannot be opened.", databaseFile);

        // The failed open left a connection in the pool, and it still holds the
        // file. Without this the move fails with a sharing violation and recovery
        // reports that another copy of the launcher is running, which is both
        // wrong and unactionable.
        SqliteConnection.ClearAllPools();

        try
        {
            // The write-ahead log and shared-memory file belong to the database
            // and are meaningless without it, so they move together. Leaving a
            // stale -wal beside a fresh database would corrupt the new one too.
            MoveAside(databaseFile, preserved);
            MoveAside(databaseFile + "-wal", preserved + "-wal");
            MoveAside(databaseFile + "-shm", preserved + "-shm");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The library database at {databaseFile} is damaged, and it could not be moved aside " +
                "to build a new one. Close any other copy of GameLauncher and try again.", ex);
        }

        _logger.LogWarning("Damaged database preserved as {Path}; starting with an empty library.", preserved);

        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _notices.Add(
            "Your library database was damaged and could not be read, so GameLauncher has started with an " +
            $"empty library. The damaged file was kept as {Path.GetFileName(preserved)} in the same folder. " +
            "Installed games are untouched — add them again to restore your library.");
    }

    /// <summary>Renames a file if it exists.</summary>
    /// <param name="from">Existing path.</param>
    /// <param name="to">Destination path.</param>
    private static void MoveAside(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to, overwrite: true);
        }
    }
}
