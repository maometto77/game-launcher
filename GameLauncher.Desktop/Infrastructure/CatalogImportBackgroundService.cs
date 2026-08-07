using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery.Import;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// When the background refresh wakes up.
/// </summary>
/// <param name="StartupDelay">
/// How long to wait after startup before the first check. Long enough that the
/// first screen has been drawn and the library has loaded; an import competing
/// with first paint would be felt as the launcher being slow to start.
/// </param>
/// <param name="PollInterval">How often to wake and see whether a refresh is due.</param>
/// <remarks>
/// Injectable so the loop can be exercised in a test without waiting real
/// minutes for it. Nothing else varies it.
/// </remarks>
public sealed record CatalogRefreshSchedule(TimeSpan StartupDelay, TimeSpan PollInterval)
{
    /// <summary>The schedule used in the running application.</summary>
    public static CatalogRefreshSchedule Default { get; } =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
}

/// <summary>
/// Refreshes the discovery catalogue in the background.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="RelayCoordinatorService"/>, and for the same reason:
/// nothing about an optional feature may delay the window opening.
/// <see cref="StartAsync"/> returns immediately and the work happens on a
/// background loop, so a slow or unreachable source costs the user nothing.
/// </para>
/// <para>
/// Registered last of the hosted services. It depends on settings and the
/// database being ready, and nothing depends on it.
/// </para>
/// </remarks>
public sealed class CatalogImportBackgroundService : IHostedService, IDisposable
{
    private readonly ICatalogImportService _import;
    private readonly ICatalogListingRepository _repository;
    private readonly ISettingsService _settings;
    private readonly CatalogRefreshSchedule _schedule;
    private readonly ILogger<CatalogImportBackgroundService> _logger;

    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="import">Runs the passes.</param>
    /// <param name="repository">Supplies when each source last ran.</param>
    /// <param name="settings">Supplies whether discovery is on and how often to refresh.</param>
    /// <param name="logger">Logger for background import diagnostics.</param>
    /// <param name="schedule">When to wake, or <see langword="null"/> for the application's schedule.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CatalogImportBackgroundService(
        ICatalogImportService import,
        ICatalogListingRepository repository,
        ISettingsService settings,
        ILogger<CatalogImportBackgroundService> logger,
        CatalogRefreshSchedule? schedule = null)
    {
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schedule = schedule ?? CatalogRefreshSchedule.Default;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns as soon as the loop is launched. It is deliberately not awaited:
    /// awaiting it here would hold up every hosted service after this one and
    /// delay the shell window by however long a refresh takes.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunLoopAsync(_stopping.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_loop is null)
        {
            return;
        }

        try
        {
            // Bounded by the host's own shutdown token: a refresh mid-flight must
            // not keep the process alive, and its cursor makes resuming free.
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _logger.LogDebug("The catalogue import loop was still running at shutdown.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Dispose();
    }

    /// <summary>
    /// Waits, then refreshes whenever a refresh is due, until shutdown.
    /// </summary>
    /// <param name="cancellationToken">Signals shutdown.</param>
    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_schedule.StartupDelay, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshIfDueAsync(cancellationToken).ConfigureAwait(false);

                await Task.Delay(_schedule.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            // The loop is the last line of defence. An unhandled exception here
            // would take down a background thread and, with it, every future
            // refresh — with nothing in the log to say why.
            _logger.LogError(ex, "The catalogue import loop stopped unexpectedly.");
        }
    }

    /// <summary>
    /// Runs a pass if one is due.
    /// </summary>
    /// <param name="cancellationToken">Signals shutdown.</param>
    private async Task RefreshIfDueAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        if (!settings.DiscoveryEnabled)
        {
            return;
        }

        var available = _import.Sources.Where(source => source.IsAvailable).ToArray();

        if (available.Length == 0)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, settings.DiscoveryRefreshHours));
        var due = new List<string>();

        foreach (var source in available)
        {
            var last = await _repository.GetLastRunAsync(source.Key, cancellationToken).ConfigureAwait(false);

            // Never run, or interrupted, or it failed, or enough time has passed.
            //
            // A failed run counts as due again rather than satisfying the
            // interval. Treating a failure as "done for a day" would mean a
            // transient outage during the nightly refresh quietly cost a whole
            // day of updates, and the failure would look identical to success.
            if (last is null ||
                last.CompletedAt is null ||
                last.LastError is not null ||
                DateTimeOffset.Now - last.StartedAt >= interval)
            {
                due.Add(source.Key);
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        if (_import.IsRunning)
        {
            _logger.LogDebug("A catalogue import is already running; skipping this check.");
            return;
        }

        _logger.LogInformation("Refreshing the catalogue from {Sources}.", string.Join(", ", due));

        try
        {
            var result = await _import
                .RunAsync(new ImportRunOptions { SourceKeys = due, Mode = ImportMode.Incremental },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (result.HasChanges)
            {
                _logger.LogInformation(
                    "Catalogue refreshed: {Added} new, {Changed} updated.",
                    result.ListingsAdded, result.ItemsChanged);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // Something else started a pass between the check and the call. Not
            // worth logging as an error; the next poll will pick it up.
        }
        catch (Exception ex)
        {
            // A failed refresh is not a failed launcher. The next poll tries
            // again, and the cursor means it resumes rather than starting over.
            _logger.LogWarning(ex, "A background catalogue refresh failed.");
        }
    }
}
