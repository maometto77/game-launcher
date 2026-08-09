using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the downloads queue.
/// </summary>
/// <remarks>
/// Presents what the queue is doing and passes the user's commands back to it.
/// It holds no state of its own beyond the rows: the queue is a singleton and
/// the authority on what is running, so navigating away and back shows exactly
/// what is still happening.
/// </remarks>
public sealed partial class DownloadsViewModel : ViewModelBase
{
    private readonly IDownloadQueue _queue;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<DownloadsViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<DownloadItemViewModel> _items = [];

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>Combined transfer rate across everything downloading.</summary>
    [ObservableProperty]
    private string _totalSpeedText = "—";

    /// <summary>How many downloads are waiting for a slot.</summary>
    [ObservableProperty]
    private string _queuedCountText = "0";

    /// <summary>
    /// When everything currently queued should be finished.
    /// </summary>
    /// <remarks>
    /// Computed from the bytes still outstanding across every job over the
    /// combined rate, rather than by adding up each job's own estimate. Those
    /// estimates assume each job has the whole connection to itself, so summing
    /// them roughly doubles the answer whenever two are running.
    /// </remarks>
    [ObservableProperty]
    private string _estimatedCompletionText = "—";

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="queue">The queue being presented.</param>
    /// <param name="dialogs">Confirmation prompts and the executable picker.</param>
    /// <param name="dispatcher">Marshals queue notifications onto the interface thread.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DownloadsViewModel(
        IDownloadQueue queue,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        ILogger<DownloadsViewModel> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        _queue.JobChanged += OnJobChanged;

        Rebuild();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnNavigatedFromAsync()
    {
        _queue.JobChanged -= OnJobChanged;

        return Task.CompletedTask;
    }

    /// <summary>Holds a download.</summary>
    /// <param name="item">The row to pause.</param>
    [RelayCommand]
    private void Pause(DownloadItemViewModel? item)
    {
        if (item is not null)
        {
            _queue.Pause(item.JobId);
        }
    }

    /// <summary>Returns a held download to the queue.</summary>
    /// <param name="item">The row to resume.</param>
    [RelayCommand]
    private void Resume(DownloadItemViewModel? item)
    {
        if (item is not null)
        {
            _queue.Resume(item.JobId);
        }
    }

    /// <summary>Stops a download for good.</summary>
    /// <param name="item">The row to cancel.</param>
    [RelayCommand]
    private void Cancel(DownloadItemViewModel? item)
    {
        // Confirmed because it discards work: a part-finished transfer is thrown
        // away, and on a large archive that is a lot to lose by mis-clicking.
        if (item is not null &&
            _dialogs.Confirm("Cancel download", $"Stop downloading '{item.Title}'?", isDestructive: true))
        {
            _queue.Cancel(item.JobId);
        }
    }

    /// <summary>Starts a stopped download again.</summary>
    /// <param name="item">The row to retry.</param>
    [RelayCommand]
    private void Retry(DownloadItemViewModel? item)
    {
        if (item is not null)
        {
            _queue.Retry(item.JobId);
        }
    }

    /// <summary>Removes a finished row from the list.</summary>
    /// <param name="item">The row to remove.</param>
    [RelayCommand]
    private void Remove(DownloadItemViewModel? item)
    {
        if (item is not null)
        {
            _queue.Remove(item.JobId);
        }
    }

    /// <summary>Moves a waiting download earlier in the queue.</summary>
    /// <param name="item">The row to move.</param>
    [RelayCommand]
    private void MoveUp(DownloadItemViewModel? item)
    {
        if (item is not null)
        {
            _queue.Reorder(item.JobId, -1);
        }
    }

    /// <summary>Moves a waiting download later in the queue.</summary>
    /// <param name="item">The row to move.</param>
    [RelayCommand]
    private void MoveDown(DownloadItemViewModel? item)
    {
        if (item is not null)
        {
            _queue.Reorder(item.JobId, 1);
        }
    }

    /// <summary>Removes every stopped row.</summary>
    [RelayCommand]
    private void ClearFinished() => _queue.ClearFinished();

    /// <summary>
    /// Adds a finished download's game to the library.
    /// </summary>
    /// <param name="item">The row to install.</param>
    /// <returns>A task that completes once the game has been added or abandoned.</returns>
    /// <remarks>
    /// The user picks which executable when there is more than one candidate.
    /// Choosing for them would be a guess presented as a decision, which is the
    /// same reason the folder scan asks.
    /// </remarks>
    [RelayCommand]
    private async Task InstallAsync(DownloadItemViewModel? item)
    {
        if (item is null || !item.CanInstall)
        {
            return;
        }

        var candidates = item.Job.Candidates;

        var executable = candidates.Count == 1
            ? candidates[0].ExecutablePath
            : _dialogs.PickFile(
                $"Choose what launches {item.Title}",
                "Executables|*.exe|All files|*.*",
                item.Job.InstallDirectory);

        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        try
        {
            var game = await _queue.CompleteAsync(item.JobId, executable).ConfigureAwait(true);

            if (game is null)
            {
                SetErrorMessage($"'{item.Title}' could not be added to your library.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adding '{Title}' to the library failed.", item.Title);
            SetErrorMessage($"Adding '{item.Title}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reacts to the queue reporting a change.
    /// </summary>
    /// <param name="sender">The queue.</param>
    /// <param name="e">The job that changed.</param>
    /// <remarks>
    /// Raised on whichever thread the transfer is running on, so it is marshalled
    /// before anything bound is touched. A row that already exists is refreshed
    /// rather than replaced, so the list does not flicker several times a second
    /// while something is downloading.
    /// </remarks>
    private void OnJobChanged(object? sender, DownloadJobEventArgs e) =>
        _dispatcher.Invoke(() =>
        {
            var existing = Items.FirstOrDefault(item =>
                string.Equals(item.JobId, e.Job.JobId, StringComparison.Ordinal));

            var known = _queue.Jobs.Any(job =>
                string.Equals(job.JobId, e.Job.JobId, StringComparison.Ordinal));

            if (existing is null || !known || Items.Count != _queue.Jobs.Count)
            {
                Rebuild();
                return;
            }

            existing.Refresh();
            UpdateSummary();
        });

    /// <summary>Rebuilds the row list from the queue.</summary>
    private void Rebuild()
    {
        Items = new ObservableCollection<DownloadItemViewModel>(
            _queue.Jobs.Select(job => new DownloadItemViewModel(job)));

        UpdateSummary();
    }

    /// <summary>
    /// Works out when everything outstanding should be done.
    /// </summary>
    /// <param name="jobs">Every job in the queue.</param>
    /// <param name="rate">Combined current rate, in bytes per second.</param>
    /// <returns>A short phrase, or an em dash when it cannot be estimated.</returns>
    private static string DescribeCompletion(IReadOnlyList<DownloadJob> jobs, double rate)
    {
        if (rate <= 0)
        {
            return "—";
        }

        // Only jobs whose size is known can contribute. One unknown among them
        // makes the total a lower bound, which is still worth showing — an
        // estimate that disappears whenever a server omits a length would be
        // less useful than one that is occasionally optimistic.
        var outstanding = jobs
            .Where(job => !job.IsTerminal && job.TotalBytes is > 0)
            .Sum(job => Math.Max(0, job.TotalBytes!.Value - job.BytesReceived));

        if (outstanding <= 0)
        {
            return "—";
        }

        var remaining = TimeSpan.FromSeconds(outstanding / rate);

        return remaining.TotalHours >= 1
            ? $"about {(int)remaining.TotalHours}h {remaining.Minutes}m"
            : remaining.TotalMinutes >= 1
                ? $"about {(int)remaining.TotalMinutes}m"
                : "less than a minute";
    }

    /// <summary>Recomputes the header line.</summary>
    private void UpdateSummary()
    {
        var jobs = _queue.Jobs;

        IsEmpty = jobs.Count == 0;

        if (IsEmpty)
        {
            SummaryText = string.Empty;
            TotalSpeedText = "—";
            QueuedCountText = "0";
            EstimatedCompletionText = "—";
            return;
        }

        var active = jobs.Count(job => job.IsActive);
        var waiting = jobs.Count(job => job.Phase == DownloadPhase.Queued);
        var done = jobs.Count(job => job.Phase == DownloadPhase.Completed);

        var rate = jobs
            .Where(job => job.Phase == DownloadPhase.Downloading)
            .Sum(job => job.BytesPerSecond);

        TotalSpeedText = rate > 0 ? $"{rate / (1024 * 1024):0.0} MB/s" : "—";
        QueuedCountText = waiting.ToString(System.Globalization.CultureInfo.CurrentCulture);
        EstimatedCompletionText = DescribeCompletion(jobs, rate);

        var parts = new List<string> { $"{active} active" };

        if (waiting > 0)
        {
            parts.Add($"{waiting} queued");
        }

        if (done > 0)
        {
            parts.Add($"{done} installed");
        }

        if (rate > 0)
        {
            parts.Add($"{rate / (1024 * 1024):0.#} MB/s total");
        }

        SummaryText = string.Join(" · ", parts);
    }
}
