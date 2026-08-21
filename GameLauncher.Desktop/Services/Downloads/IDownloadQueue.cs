using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Downloads;

/// <summary>
/// Reports that a job changed.
/// </summary>
/// <param name="job">The job that changed.</param>
public sealed class DownloadJobEventArgs(DownloadJob job) : EventArgs
{
    /// <summary>Gets the job that changed.</summary>
    public DownloadJob Job { get; } = job;
}

/// <summary>
/// Runs catalogue installs in the background, a few at a time.
/// </summary>
/// <remarks>
/// <para>
/// The piece that turns installing into something a person can manage. Without a
/// queue there is nothing for pause, resume, cancel, retry or reordering to act
/// on — <see cref="Discovery.Install.IListingInstallService"/> is one call that
/// either finishes or throws.
/// </para>
/// <para>
/// The queue owns every write to a <see cref="DownloadJob"/>. Callers read them
/// and subscribe to <see cref="JobChanged"/>; nothing outside mutates one, so
/// there is exactly one writer and the interface never has to reason about a
/// half-applied update.
/// </para>
/// <para>
/// Not persisted across restarts. A queue that promised to resume work whose
/// temporary state may be gone would be lying; the <c>.part</c> file on disk is
/// what actually makes a transfer resumable, and re-queueing costs one click.
/// </para>
/// </remarks>
public interface IDownloadQueue
{
    /// <summary>Raised whenever a job is added, changes phase, or reports progress.</summary>
    /// <remarks>
    /// Raised on whichever thread the work is running on. Subscribers touching
    /// the interface must marshal, which is what <c>IUiDispatcher</c> is for.
    /// </remarks>
    event EventHandler<DownloadJobEventArgs>? JobChanged;

    /// <summary>Gets every job, in the order they should be shown.</summary>
    IReadOnlyList<DownloadJob> Jobs { get; }

    /// <summary>
    /// Gets how many downloads may run at once.
    /// </summary>
    int MaxConcurrent { get; }

    /// <summary>
    /// Adds a listing to the queue.
    /// </summary>
    /// <param name="listingId">The listing to install.</param>
    /// <param name="title">Title to show in the queue.</param>
    /// <returns>
    /// The new job, or the existing one when the listing is already queued —
    /// queueing the same game twice would have two transfers writing to one
    /// <c>.part</c> file.
    /// </returns>
    /// <param name="preferredSourceKey">
    /// A source whose addresses should be tried first, or <see langword="null"/>
    /// to take them in the order the catalogue recorded.
    /// </param>
    /// <exception cref="ArgumentException">Either required argument is null or blank.</exception>
    DownloadJob Enqueue(string listingId, string title, string? preferredSourceKey = null);

    /// <summary>
    /// Holds a job. A running one is stopped; its partial transfer is kept.
    /// </summary>
    /// <param name="jobId">The job to pause.</param>
    /// <returns><see langword="true"/> when the job was paused.</returns>
    bool Pause(string jobId);

    /// <summary>Returns a paused or failed job to the queue.</summary>
    /// <param name="jobId">The job to resume.</param>
    /// <returns><see langword="true"/> when the job was queued again.</returns>
    bool Resume(string jobId);

    /// <summary>Stops a job for good.</summary>
    /// <param name="jobId">The job to cancel.</param>
    /// <returns><see langword="true"/> when the job was cancelled.</returns>
    bool Cancel(string jobId);

    /// <summary>Queues a finished-but-unsuccessful job again from the start.</summary>
    /// <param name="jobId">The job to retry.</param>
    /// <returns><see langword="true"/> when the job was queued again.</returns>
    bool Retry(string jobId);

    /// <summary>Removes a job that is no longer running from the list.</summary>
    /// <param name="jobId">The job to remove.</param>
    /// <returns><see langword="true"/> when it was removed.</returns>
    bool Remove(string jobId);

    /// <summary>Removes every job that has stopped.</summary>
    /// <returns>How many were removed.</returns>
    int ClearFinished();

    /// <summary>
    /// Moves a job up or down the queue.
    /// </summary>
    /// <param name="jobId">The job to move.</param>
    /// <param name="offset">Negative to move earlier, positive to move later.</param>
    /// <returns><see langword="true"/> when the order changed.</returns>
    /// <remarks>
    /// Only affects jobs that have not started. Reordering something already
    /// transferring would either mean abandoning its progress or doing nothing,
    /// and doing nothing while appearing to work is the worse of the two.
    /// </remarks>
    bool Reorder(string jobId, int offset);

    /// <summary>
    /// Adds a finished job's chosen executable to the library.
    /// </summary>
    /// <param name="jobId">The job to complete.</param>
    /// <param name="executablePath">The executable the user picked.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The imported game, or <see langword="null"/> when the import failed.</returns>
    /// <remarks>
    /// Separate from the download because the existing install path deliberately
    /// stops short of registering anything: choosing which executable a game
    /// launches is the user's, not a guess presented as a decision.
    /// </remarks>
    Task<Game?> CompleteAsync(
        string jobId,
        string executablePath,
        CancellationToken cancellationToken = default);
}
