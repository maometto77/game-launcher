using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery.Install;
using GameLauncher.Desktop.Services.Download;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Downloads;

/// <summary>
/// Default <see cref="IDownloadQueue"/>.
/// </summary>
/// <remarks>
/// <para>
/// A pump rather than a thread pool: whenever a slot frees, the highest-priority
/// queued job starts. Everything that mutates a job happens under one lock, so
/// the interface only ever observes a consistent state — and because the queue
/// is the sole writer, a view model can bind straight to a job.
/// </para>
/// <para>
/// Pause and cancel are the same mechanism, a per-job cancellation token, and
/// differ only in the phase they leave behind. That matters for correctness:
/// a paused transfer keeps its <c>.part</c> file, so resuming continues rather
/// than starting over, exactly as an interrupted download always has.
/// </para>
/// </remarks>
public sealed class DownloadQueue : IDownloadQueue, IDisposable
{
    /// <summary>How far apart two jobs' priorities are placed.</summary>
    /// <remarks>
    /// Sparse so a job can be moved between two others without renumbering the
    /// rest of the queue.
    /// </remarks>
    private const int PriorityStep = 100;

    private readonly IListingInstallService _install;
    private readonly ICatalogListingRepository _listings;
    private readonly ILogger<DownloadQueue> _logger;

    private readonly object _gate = new();
    private readonly List<DownloadJob> _jobs = [];
    private readonly Dictionary<string, CancellationTokenSource> _running = [];

    private int _nextPriority;
    private int _sequence;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="install">Performs the download, verify, unpack and detect.</param>
    /// <param name="listings">Supplies the listing a completed job installs.</param>
    /// <param name="logger">Logger for queue diagnostics.</param>
    /// <param name="maxConcurrent">How many downloads may run at once.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DownloadQueue(
        IListingInstallService install,
        ICatalogListingRepository listings,
        ILogger<DownloadQueue> logger,
        int maxConcurrent = 2)
    {
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _listings = listings ?? throw new ArgumentNullException(nameof(listings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Two by default. More parallel downloads rarely finish sooner on one
        // connection and they make every individual one look stalled.
        MaxConcurrent = Math.Clamp(maxConcurrent, 1, 8);
    }

    /// <inheritdoc />
    public event EventHandler<DownloadJobEventArgs>? JobChanged;

    /// <inheritdoc />
    public int MaxConcurrent { get; }

    /// <inheritdoc />
    public IReadOnlyList<DownloadJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs
                    .OrderBy(job => job.IsTerminal ? 1 : 0)
                    .ThenBy(job => job.Priority)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public DownloadJob Enqueue(string listingId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        DownloadJob job;

        lock (_gate)
        {
            // Two jobs for one listing would be two transfers writing to the same
            // .part file, which corrupts both.
            var existing = _jobs.FirstOrDefault(candidate =>
                string.Equals(candidate.ListingId, listingId, StringComparison.Ordinal) &&
                !candidate.IsTerminal);

            if (existing is not null)
            {
                return existing;
            }

            job = new DownloadJob
            {
                JobId = $"job_{++_sequence}",
                ListingId = listingId,
                Title = title,
                Priority = _nextPriority += PriorityStep
            };

            _jobs.Add(job);
        }

        _logger.LogInformation("Queued '{Title}'.", title);

        Raise(job);
        Pump();

        return job;
    }

    /// <inheritdoc />
    public bool Pause(string jobId) => Stop(jobId, DownloadPhase.Paused);

    /// <inheritdoc />
    public bool Cancel(string jobId) => Stop(jobId, DownloadPhase.Cancelled);

    /// <inheritdoc />
    public bool Resume(string jobId)
    {
        DownloadJob? job;

        lock (_gate)
        {
            job = Find(jobId);

            if (job is null || job.Phase is not (DownloadPhase.Paused or DownloadPhase.Failed))
            {
                return false;
            }

            job.Phase = DownloadPhase.Queued;
            job.Error = null;
            job.StatusMessage = null;
        }

        Raise(job);
        Pump();

        return true;
    }

    /// <inheritdoc />
    public bool Retry(string jobId)
    {
        DownloadJob? job;

        lock (_gate)
        {
            job = Find(jobId);

            if (job is null || !job.IsTerminal)
            {
                return false;
            }

            job.Phase = DownloadPhase.Queued;
            job.Error = null;
            job.StatusMessage = null;
            job.BytesReceived = 0;
            job.BytesPerSecond = 0;
            job.MirrorsTried = 0;
            job.Candidates = [];
        }

        Raise(job);
        Pump();

        return true;
    }

    /// <inheritdoc />
    public bool Remove(string jobId)
    {
        DownloadJob? job;

        lock (_gate)
        {
            job = Find(jobId);

            // A running job is stopped by cancelling it, not by being taken off
            // the list underneath itself.
            if (job is null || _running.ContainsKey(jobId))
            {
                return false;
            }

            _jobs.Remove(job);
        }

        Raise(job);

        return true;
    }

    /// <inheritdoc />
    public int ClearFinished()
    {
        DownloadJob[] removed;

        lock (_gate)
        {
            removed = _jobs.Where(job => job.IsTerminal).ToArray();

            foreach (var job in removed)
            {
                _jobs.Remove(job);
            }
        }

        foreach (var job in removed)
        {
            Raise(job);
        }

        return removed.Length;
    }

    /// <inheritdoc />
    public bool Reorder(string jobId, int offset)
    {
        if (offset == 0)
        {
            return false;
        }

        DownloadJob? job;

        lock (_gate)
        {
            job = Find(jobId);

            // Only jobs that have not started can move. Reordering a running one
            // would mean abandoning its progress or doing nothing while
            // appearing to work.
            if (job is null || job.Phase is not (DownloadPhase.Queued or DownloadPhase.Paused))
            {
                return false;
            }

            var movable = _jobs
                .Where(candidate => candidate.Phase is DownloadPhase.Queued or DownloadPhase.Paused)
                .OrderBy(candidate => candidate.Priority)
                .ToList();

            var index = movable.IndexOf(job);
            var target = Math.Clamp(index + offset, 0, movable.Count - 1);

            if (index == target)
            {
                return false;
            }

            movable.RemoveAt(index);
            movable.Insert(target, job);

            // Renumbered across the movable set so the new order is unambiguous,
            // whatever the previous priorities happened to be.
            for (var position = 0; position < movable.Count; position++)
            {
                movable[position].Priority = (position + 1) * PriorityStep;
            }

            _nextPriority = movable.Count * PriorityStep;
        }

        Raise(job);

        return true;
    }

    /// <inheritdoc />
    public async Task<Game?> CompleteAsync(
        string jobId,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        DownloadJob? job;

        lock (_gate)
        {
            job = Find(jobId);
        }

        if (job is null || job.Phase != DownloadPhase.ReadyToInstall || job.InstallDirectory is null)
        {
            return null;
        }

        var listing = await _listings.GetAsync(job.ListingId, cancellationToken).ConfigureAwait(false);

        if (listing is null)
        {
            return null;
        }

        var game = await _install
            .CompleteAsync(listing, executablePath, job.InstallDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (game is not null)
        {
            job.Phase = DownloadPhase.Completed;
            job.StatusMessage = $"Added '{game.Title}' to your library.";

            Raise(job);
        }

        return game;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CancellationTokenSource[] running;

        lock (_gate)
        {
            running = [.. _running.Values];
            _running.Clear();
        }

        foreach (var source in running)
        {
            try
            {
                source.Cancel();
                source.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already finished and cleaned itself up.
            }
        }
    }

    /// <summary>
    /// Stops a running or queued job, leaving it in a given phase.
    /// </summary>
    /// <param name="jobId">The job to stop.</param>
    /// <param name="phase">The phase to leave it in.</param>
    /// <returns><see langword="true"/> when the job was stopped.</returns>
    private bool Stop(string jobId, DownloadPhase phase)
    {
        DownloadJob? job;
        CancellationTokenSource? cancellation = null;

        lock (_gate)
        {
            job = Find(jobId);

            if (job is null || job.IsTerminal || job.Phase == phase)
            {
                return false;
            }

            if (_running.Remove(jobId, out var source))
            {
                cancellation = source;
            }

            job.Phase = phase;
            job.BytesPerSecond = 0;
        }

        // Cancelled outside the lock: the worker's continuation also takes it,
        // and cancelling under it would invite a deadlock.
        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // The worker finished first.
            }
        }

        Raise(job);
        Pump();

        return true;
    }

    /// <summary>
    /// Starts as many queued jobs as there are free slots.
    /// </summary>
    private void Pump()
    {
        while (true)
        {
            DownloadJob? next;
            CancellationTokenSource cancellation;

            lock (_gate)
            {
                if (_disposed || _running.Count >= MaxConcurrent)
                {
                    return;
                }

                next = _jobs
                    .Where(job => job.Phase == DownloadPhase.Queued)
                    .OrderBy(job => job.Priority)
                    .FirstOrDefault();

                if (next is null)
                {
                    return;
                }

                cancellation = new CancellationTokenSource();
                _running[next.JobId] = cancellation;

                next.Phase = DownloadPhase.Resolving;
                next.StatusMessage = "Finding a download…";
            }

            Raise(next);

            // Deliberately not awaited: the pump starts work and returns so the
            // caller — often a button click — is never blocked by a transfer.
            _ = RunAsync(next, cancellation.Token);
        }
    }

    /// <summary>
    /// Runs one job to completion.
    /// </summary>
    /// <param name="job">The job to run.</param>
    /// <param name="cancellationToken">Cancels it, for pause or cancel.</param>
    private async Task RunAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<InstallProgress>(update => Apply(job, update));

            var result = await _install
                .PrepareAsync(job.ListingId, progress, cancellationToken)
                .ConfigureAwait(false);

            job.MirrorsTried = result.MirrorsTried;

            if (!result.Succeeded)
            {
                job.Phase = DownloadPhase.Failed;
                job.Error = result.Message;
                job.StatusMessage = null;
            }
            else
            {
                job.InstallDirectory = result.Preparation!.InstallDirectory;
                job.Candidates = result.Preparation.Candidates;
                job.Phase = DownloadPhase.ReadyToInstall;

                job.StatusMessage = result.Preparation.Candidates.Count == 0
                    ? "Downloaded, but nothing to launch was found."
                    : "Ready to add to your library.";
            }
        }
        catch (OperationCanceledException)
        {
            // Pause and cancel both arrive here. The phase was already set by
            // whichever asked for it, so it is deliberately left alone.
            job.BytesPerSecond = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Downloading '{Title}' failed.", job.Title);

            job.Phase = DownloadPhase.Failed;
            job.Error = ex.Message;
            job.StatusMessage = null;
        }
        finally
        {
            lock (_gate)
            {
                if (_running.Remove(job.JobId, out var source))
                {
                    source.Dispose();
                }
            }

            Raise(job);
            Pump();
        }
    }

    /// <summary>
    /// Applies a progress report from the install path to a job.
    /// </summary>
    /// <param name="job">The job being reported on.</param>
    /// <param name="update">What the install path said.</param>
    private static void Apply(DownloadJob job, InstallProgress update)
    {
        // A report arriving after a pause must not resurrect the job's phase.
        if (job.Phase is DownloadPhase.Paused or DownloadPhase.Cancelled)
        {
            return;
        }

        job.Phase = update.Phase switch
        {
            InstallPhase.Downloading => DownloadPhase.Downloading,
            InstallPhase.Verifying => DownloadPhase.Verifying,
            InstallPhase.Extracting => DownloadPhase.Extracting,
            InstallPhase.Detecting => DownloadPhase.Detecting,
            _ => job.Phase
        };

        job.StatusMessage = update.Message;

        // The transfer's own numbers when they are available. A percentage
        // cannot be turned back into a speed or an estimate, which is why the
        // install path now carries this through rather than reducing it.
        if (update.Transfer is { } transfer)
        {
            job.BytesReceived = transfer.BytesReceived;
            job.TotalBytes = transfer.TotalBytes;
            job.BytesPerSecond = transfer.BytesPerSecond;
            job.Elapsed = transfer.Elapsed;
        }
        else if (update.Fraction is { } fraction && job.TotalBytes is > 0)
        {
            job.BytesReceived = (long)(job.TotalBytes.Value * fraction);
        }
    }

    /// <summary>Finds a job by identity. The caller holds the lock.</summary>
    /// <param name="jobId">The job to find.</param>
    /// <returns>The job, or <see langword="null"/>.</returns>
    private DownloadJob? Find(string jobId) =>
        _jobs.FirstOrDefault(job => string.Equals(job.JobId, jobId, StringComparison.Ordinal));

    /// <summary>Announces that a job changed.</summary>
    /// <param name="job">The job that changed.</param>
    private void Raise(DownloadJob job) => JobChanged?.Invoke(this, new DownloadJobEventArgs(job));
}
