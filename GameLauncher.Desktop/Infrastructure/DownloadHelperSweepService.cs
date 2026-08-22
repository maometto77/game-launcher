using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Clears download helpers left running by a launcher that did not shut down
/// cleanly.
/// </summary>
/// <remarks>
/// <para>
/// Runs once, at startup, before anything can start a download of its own. An
/// abandoned <c>aria2c</c> still holds the file a fresh attempt at the same
/// download would write to, and two of them on one file corrupt it between them,
/// so this has to happen before the queue drains rather than alongside it.
/// </para>
/// <para>
/// Separate from <see cref="DownloadHelperRegistry"/> because the register is a
/// collaborator the transport writes to throughout the session, whereas the
/// sweep is a one-off piece of startup housekeeping.
/// </para>
/// </remarks>
public sealed class DownloadHelperSweepService : IHostedService
{
    private readonly DownloadHelperRegistry _helpers;
    private readonly ILogger<DownloadHelperSweepService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="helpers">The register to sweep.</param>
    /// <param name="logger">Records that the sweep ran.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DownloadHelperSweepService(
        DownloadHelperRegistry helpers,
        ILogger<DownloadHelperSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(helpers);
        ArgumentNullException.ThrowIfNull(logger);

        _helpers = helpers;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Synchronous on purpose. It is a file read and at most a handful of
        // process lookups, and it must finish before the first download starts.
        var stopped = _helpers.Sweep();

        if (stopped == 0)
        {
            _logger.LogDebug("No abandoned download helpers to clear.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
