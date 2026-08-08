using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Install;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Desktop.Services.Downloads;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Downloads;

/// <summary>
/// Covers the queue's guarantees: concurrency, ordering, and that pause, cancel,
/// resume and retry each leave the job somewhere sensible.
/// </summary>
public sealed class DownloadQueueTests
{
    [Fact]
    public async Task A_queued_download_runs_and_becomes_ready_to_install()
    {
        using var fixture = new QueueFixture();

        var job = fixture.Queue.Enqueue("lst_1", "Doom");

        // Released only once the install has actually started, or the release
        // races the gate being registered and the job waits forever.
        Assert.True(await WaitAsync(() => job.IsActive));
        fixture.Install.Release("lst_1");

        Assert.True(await WaitAsync(() => job.Phase == DownloadPhase.ReadyToInstall));
        Assert.NotEmpty(job.Candidates);
    }

    [Fact]
    public async Task Only_the_permitted_number_run_at_once()
    {
        using var fixture = new QueueFixture(maxConcurrent: 2);

        foreach (var index in Enumerable.Range(1, 5))
        {
            fixture.Queue.Enqueue($"lst_{index}", $"Game {index}");
        }

        Assert.True(await WaitAsync(() => fixture.Install.Started.Count == 2));

        // Give any extra starts a chance to happen before asserting they did not.
        await Task.Delay(150);

        Assert.Equal(2, fixture.Install.Started.Count);
        Assert.Equal(3, fixture.Queue.Jobs.Count(job => job.Phase == DownloadPhase.Queued));
    }

    [Fact]
    public async Task Finishing_one_download_starts_the_next()
    {
        using var fixture = new QueueFixture(maxConcurrent: 1);

        fixture.Queue.Enqueue("lst_1", "First");
        fixture.Queue.Enqueue("lst_2", "Second");

        Assert.True(await WaitAsync(() => fixture.Install.Started.Contains("lst_1")));

        fixture.Install.Release("lst_1");

        Assert.True(await WaitAsync(() => fixture.Install.Started.Contains("lst_2")));
    }

    [Fact]
    public async Task Higher_priority_jobs_run_first()
    {
        using var fixture = new QueueFixture(maxConcurrent: 1);

        fixture.Queue.Enqueue("lst_1", "First");
        fixture.Queue.Enqueue("lst_2", "Second");
        var third = fixture.Queue.Enqueue("lst_3", "Third");

        Assert.True(await WaitAsync(() => fixture.Install.Started.Contains("lst_1")));

        // The last in the queue is moved ahead of the one before it, while both
        // are still waiting.
        Assert.True(fixture.Queue.Reorder(third.JobId, -1));

        fixture.Install.Release("lst_1");

        Assert.True(await WaitAsync(() => fixture.Install.Started.Contains("lst_3")));
        Assert.DoesNotContain("lst_2", fixture.Install.Started);
    }

    [Fact]
    public async Task A_running_job_cannot_be_reordered()
    {
        using var fixture = new QueueFixture(maxConcurrent: 1);

        var job = fixture.Queue.Enqueue("lst_1", "Running");
        fixture.Queue.Enqueue("lst_2", "Waiting");

        Assert.True(await WaitAsync(() => job.IsActive));

        // Doing nothing while appearing to work would be worse than refusing.
        Assert.False(fixture.Queue.Reorder(job.JobId, 1));
    }

    [Fact]
    public async Task Pausing_stops_the_transfer_and_keeps_the_job()
    {
        using var fixture = new QueueFixture();

        var job = fixture.Queue.Enqueue("lst_1", "Doom");

        Assert.True(await WaitAsync(() => job.IsActive));
        Assert.True(fixture.Queue.Pause(job.JobId));

        Assert.True(await WaitAsync(() => job.Phase == DownloadPhase.Paused));
        Assert.True(await WaitAsync(() => fixture.Install.Cancelled.Contains("lst_1")));

        // Still in the list: pausing is not cancelling.
        Assert.Contains(fixture.Queue.Jobs, candidate => candidate.JobId == job.JobId);
        Assert.False(job.IsTerminal);
    }

    [Fact]
    public async Task Resuming_a_paused_job_runs_it_again()
    {
        using var fixture = new QueueFixture();

        var job = fixture.Queue.Enqueue("lst_1", "Doom");

        Assert.True(await WaitAsync(() => job.IsActive));
        fixture.Queue.Pause(job.JobId);
        Assert.True(await WaitAsync(() => job.Phase == DownloadPhase.Paused));

        fixture.Install.Started.Clear();
        Assert.True(fixture.Queue.Resume(job.JobId));

        Assert.True(await WaitAsync(() => fixture.Install.Started.Contains("lst_1")));
    }

    [Fact]
    public async Task Cancelling_ends_the_job_for_good()
    {
        using var fixture = new QueueFixture();

        var job = fixture.Queue.Enqueue("lst_1", "Doom");

        Assert.True(await WaitAsync(() => job.IsActive));
        Assert.True(fixture.Queue.Cancel(job.JobId));

        Assert.True(await WaitAsync(() => job.Phase == DownloadPhase.Cancelled));
        Assert.True(job.IsTerminal);

        // A cancelled job cannot be resumed; retrying starts it over.
        Assert.False(fixture.Queue.Resume(job.JobId));
        Assert.True(fixture.Queue.Retry(job.JobId));
    }

    [Fact]
    public async Task A_failure_is_retryable_and_starts_from_scratch()
    {
        using var fixture = new QueueFixture();

        fixture.Install.FailFor.Add("lst_1");

        var job = fixture.Queue.Enqueue("lst_1", "Doom");

        Assert.True(await WaitAsync(() => job.Phase == DownloadPhase.Failed));
        Assert.NotNull(job.Error);

        fixture.Install.FailFor.Clear();
        Assert.True(fixture.Queue.Retry(job.JobId));

        // Released only once the retry has actually started, or it races the
        // gate being registered.
        Assert.True(await WaitAsync(() => job.IsActive));
        fixture.Install.Release("lst_1");

        Assert.True(await WaitAsync(() => job.Phase == DownloadPhase.ReadyToInstall));
        Assert.Null(job.Error);
    }

    [Fact]
    public async Task Queueing_the_same_listing_twice_returns_the_same_job()
    {
        using var fixture = new QueueFixture();

        var first = fixture.Queue.Enqueue("lst_1", "Doom");
        var second = fixture.Queue.Enqueue("lst_1", "Doom");

        // Two transfers writing to one .part file corrupts both.
        Assert.Same(first, second);
        Assert.Single(fixture.Queue.Jobs);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Progress_carries_the_speed_and_size_through()
    {
        using var fixture = new QueueFixture();

        var job = fixture.Queue.Enqueue("lst_1", "Doom");

        Assert.True(await WaitAsync(() => job.IsActive));

        fixture.Install.ReportTransfer("lst_1", received: 500, total: 2000, rate: 1024 * 1024);

        Assert.True(await WaitAsync(() => job.BytesPerSecond > 0));

        // A percentage cannot be turned back into a speed or an estimate, which
        // is why the install path carries the transfer through.
        Assert.Equal(500, job.BytesReceived);
        Assert.Equal(2000, job.TotalBytes);
        Assert.Equal(0.25, job.Fraction);
        Assert.NotNull(job.EstimatedRemaining);
    }

    [Fact]
    public async Task A_report_arriving_after_a_pause_does_not_restart_the_job()
    {
        using var fixture = new QueueFixture();

        var job = fixture.Queue.Enqueue("lst_1", "Doom");

        Assert.True(await WaitAsync(() => job.IsActive));
        fixture.Queue.Pause(job.JobId);
        Assert.True(await WaitAsync(() => job.Phase == DownloadPhase.Paused));

        fixture.Install.ReportTransfer("lst_1", received: 900, total: 2000, rate: 5000);

        await Task.Delay(80);

        Assert.Equal(DownloadPhase.Paused, job.Phase);
    }

    [Fact]
    public async Task Finished_jobs_can_be_cleared_and_running_ones_cannot_be_removed()
    {
        using var fixture = new QueueFixture(maxConcurrent: 1);

        var running = fixture.Queue.Enqueue("lst_1", "Running");
        Assert.True(await WaitAsync(() => running.IsActive));

        var cancelled = fixture.Queue.Enqueue("lst_2", "Cancelled");
        fixture.Queue.Cancel(cancelled.JobId);

        Assert.False(fixture.Queue.Remove(running.JobId));
        Assert.Equal(1, fixture.Queue.ClearFinished());
        Assert.Single(fixture.Queue.Jobs);
    }

    [Fact]
    public async Task Every_change_is_announced()
    {
        using var fixture = new QueueFixture();

        var announced = new List<DownloadPhase>();
        fixture.Queue.JobChanged += (_, e) => announced.Add(e.Job.Phase);

        var job = fixture.Queue.Enqueue("lst_1", "Doom");
        fixture.Install.Release("lst_1");

        Assert.True(await WaitAsync(() => job.Phase == DownloadPhase.ReadyToInstall));

        Assert.Contains(DownloadPhase.Queued, announced);
        Assert.Contains(DownloadPhase.ReadyToInstall, announced);
    }

    /// <summary>Polls until a condition holds or the deadline passes.</summary>
    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(15).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>A queue over an install service the test drives by hand.</summary>
    private sealed class QueueFixture : IDisposable
    {
        private readonly TestAppHost _host = new();

        public QueueFixture(int maxConcurrent = 2)
        {
            Install = new ControllableInstallService();

            Queue = new DownloadQueue(
                Install,
                _host.Resolve<Desktop.Services.Database.ICatalogListingRepository>(),
                NullLogger<DownloadQueue>.Instance,
                maxConcurrent);
        }

        public ControllableInstallService Install { get; }

        public DownloadQueue Queue { get; }

        public void Dispose()
        {
            Queue.Dispose();
            _host.Dispose();
        }
    }

    /// <summary>
    /// An install service that starts and then waits until a test releases it.
    /// </summary>
    /// <remarks>
    /// Lets the queue's own behaviour be tested without a download: every
    /// interesting case is about what the queue does while work is in flight.
    /// </remarks>
    private sealed class ControllableInstallService : IListingInstallService
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = [];
        private readonly Dictionary<string, IProgress<InstallProgress>?> _progress = [];
        private readonly object _lock = new();

        public List<string> Started { get; } = [];

        public List<string> Cancelled { get; } = [];

        public HashSet<string> FailFor { get; } = [];

        /// <summary>Lets a waiting install finish.</summary>
        public void Release(string listingId)
        {
            lock (_lock)
            {
                if (_gates.TryGetValue(listingId, out var gate))
                {
                    gate.TrySetResult();
                }
            }
        }

        /// <summary>Reports transfer detail as the real install path would.</summary>
        public void ReportTransfer(string listingId, long received, long? total, double rate)
        {
            IProgress<InstallProgress>? progress;

            lock (_lock)
            {
                _progress.TryGetValue(listingId, out progress);
            }

            progress?.Report(new InstallProgress(
                InstallPhase.Downloading,
                total is > 0 ? (double)received / total.Value : null,
                "Transferring…",
                new DownloadProgress(received, total, rate, TimeSpan.FromSeconds(1))));
        }

        public IReadOnlyList<ListingMirror> GetMirrors(CatalogListing listing) => [];

        public async Task<ListingInstallResult> PrepareAsync(
            string listingId,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource gate;

            lock (_lock)
            {
                Started.Add(listingId);
                _progress[listingId] = progress;

                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates[listingId] = gate;
            }

            progress?.Report(new InstallProgress(InstallPhase.Downloading, 0, "Connecting…"));

            if (FailFor.Contains(listingId))
            {
                return new ListingInstallResult(null, Listing(listingId), 1, "simulated failure");
            }

            using (cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    Cancelled.Add(listingId);
                }

                gate.TrySetCanceled(cancellationToken);
            }))
            {
                await gate.Task.ConfigureAwait(false);
            }

            return new ListingInstallResult(
                new InstallPreparationResult(
                    @"C:\Games\Fake",
                    null,
                    true,
                    [new DiscoveredGame
                    {
                        Executable = new ExecutableInfo(
                            @"C:\Games\Fake\game.exe",
                            "game.exe",
                            "Fake Game",
                            null,
                            null,
                            null,
                            null,
                            1024,
                            ExecutableArchitecture.X64,
                            ExecutableSubsystem.WindowsGui,
                            IsValidExecutable: true),
                        InstallDirectory = @"C:\Games\Fake"
                    }],
                    null),
                Listing(listingId),
                1,
                null);
        }

        public Task<Game?> CompleteAsync(
            CatalogListing listing,
            string executablePath,
            string installDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Game?>(new Game { Title = listing.Title, ExecutablePath = executablePath });

        private static CatalogListing Listing(string listingId) => new()
        {
            ListingId = listingId,
            Title = listingId,
            SortTitle = listingId,
            PrimarySourceKey = "test"
        };
    }
}
