using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One queued download, formatted for display.
/// </summary>
/// <remarks>
/// Wraps a <see cref="DownloadJob"/> the queue owns and re-reads it whenever the
/// queue says it changed. The job is never mutated here — the queue is its only
/// writer, which is what keeps a half-applied update off the screen.
/// </remarks>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="job">The job to present.</param>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.</exception>
    public DownloadItemViewModel(DownloadJob job)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
    }

    /// <summary>Gets the job behind this row.</summary>
    public DownloadJob Job { get; }

    /// <summary>Gets the job's identity.</summary>
    public string JobId => Job.JobId;

    /// <summary>Gets the title being downloaded.</summary>
    public string Title => Job.Title;

    /// <summary>Gets the phase as a word a person recognises.</summary>
    public string PhaseText => Job.Phase switch
    {
        DownloadPhase.Queued => "Queued",
        DownloadPhase.Paused => "Paused",
        DownloadPhase.Resolving => "Finding a source",
        DownloadPhase.Downloading => "Downloading",
        DownloadPhase.Verifying => "Verifying",
        DownloadPhase.Extracting => "Extracting",
        DownloadPhase.Detecting => "Detecting",
        DownloadPhase.ReadyToInstall => "Ready to install",
        DownloadPhase.Completed => "Installed",
        DownloadPhase.Cancelled => "Cancelled",
        _ => "Failed"
    };

    /// <summary>Gets completion as a percentage for a progress bar.</summary>
    public double Percent => (Job.Fraction ?? 0) * 100;

    /// <summary>Gets a value indicating whether the total size is unknown.</summary>
    /// <remarks>
    /// A bar that sits at zero because nobody knows the size looks stalled, so
    /// the view shows an indeterminate one instead.
    /// </remarks>
    public bool IsIndeterminate => Job.Fraction is null && Job.IsActive;

    /// <summary>Gets the transferred and total size, as text.</summary>
    public string SizeText => Job.TotalBytes is > 0
        ? $"{Format(Job.BytesReceived)} of {Format(Job.TotalBytes.Value)}"
        : Job.BytesReceived > 0 ? Format(Job.BytesReceived) : string.Empty;

    /// <summary>Gets the current rate, as text.</summary>
    public string SpeedText =>
        Job.Phase == DownloadPhase.Downloading && Job.BytesPerSecond > 0
            ? $"{Format((long)Job.BytesPerSecond)}/s"
            : string.Empty;

    /// <summary>Gets the estimated time remaining, as text.</summary>
    public string EtaText => Job.EstimatedRemaining is { } remaining
        ? remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m left"
            : remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s left"
                : $"{remaining.Seconds}s left"
        : string.Empty;

    /// <summary>Gets the connection counts, as text.</summary>
    /// <remarks>
    /// Seeders are shown beside peers only for a torrent, because only a torrent
    /// has them. Empty for a transport that reports neither, so an ordinary HTTP
    /// download does not carry a column of nothing.
    /// </remarks>
    public string PeersText => Job.Peers is not { } peers
        ? string.Empty
        : Job.Seeders is { } seeders
            ? $"{peers} peers · {seeders} seeds"
            : $"{peers} peers";

    /// <summary>Gets the line of detail under the title.</summary>
    public string DetailText => Job.Error ?? Job.StatusMessage ?? string.Empty;

    /// <summary>Gets a value indicating whether pausing is possible.</summary>
    public bool CanPause => Job.IsActive || Job.Phase == DownloadPhase.Queued;

    /// <summary>Gets a value indicating whether resuming is possible.</summary>
    public bool CanResume => Job.Phase is DownloadPhase.Paused or DownloadPhase.Failed;

    /// <summary>Gets a value indicating whether cancelling is possible.</summary>
    public bool CanCancel => !Job.IsTerminal;

    /// <summary>Gets a value indicating whether retrying is possible.</summary>
    public bool CanRetry => Job.Phase is DownloadPhase.Failed or DownloadPhase.Cancelled;

    /// <summary>Gets a value indicating whether the row can be reordered.</summary>
    public bool CanReorder => Job.Phase is DownloadPhase.Queued or DownloadPhase.Paused;

    /// <summary>Gets a value indicating whether the job is waiting to be added to the library.</summary>
    public bool CanInstall =>
        Job.Phase == DownloadPhase.ReadyToInstall && Job.Candidates.Count > 0;

    /// <summary>Gets a value indicating whether the row can be removed from the list.</summary>
    public bool CanRemove => Job.IsTerminal;

    /// <summary>Gets a value indicating whether the job failed.</summary>
    public bool HasFailed => Job.Phase == DownloadPhase.Failed;

    /// <summary>
    /// Tells the interface every displayed value may have changed.
    /// </summary>
    /// <remarks>
    /// One notification rather than a property-by-property comparison. The job is
    /// a plain model the queue mutates in place, so there is nothing to diff
    /// against, and a download row is cheap to re-read.
    /// </remarks>
    public void Refresh()
    {
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(PeersText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(HasFailed));
    }

    /// <summary>Formats a byte count the way a person reads one.</summary>
    /// <param name="bytes">The count to format.</param>
    /// <returns>A short string such as <c>1.4 GB</c>.</returns>
    private static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}
