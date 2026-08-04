using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Launcher;
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
    private readonly StartupOptions _options;
    private readonly ILogger<DatabaseStartupService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="initializer">Performs the schema migration.</param>
    /// <param name="seeder">Populates sample data when requested.</param>
    /// <param name="launcher">Used to close out sessions left open by a previous run.</param>
    /// <param name="catalog">Used to repair catalog entries left without a fingerprint.</param>
    /// <param name="options">Parsed command-line options.</param>
    /// <param name="logger">Logger for startup diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DatabaseStartupService(
        IDatabaseInitializer initializer,
        ISampleDataSeeder seeder,
        IGameLaunchService launcher,
        ICatalogService catalog,
        StartupOptions options,
        ILogger<DatabaseStartupService> logger)
    {
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _seeder = seeder ?? throw new ArgumentNullException(nameof(seeder));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Preparing the local database.");
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

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
}
