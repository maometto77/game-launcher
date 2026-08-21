namespace GameLauncher.Desktop.Models;

/// <summary>
/// Where a queued download has got to.
/// </summary>
/// <remarks>
/// Ordered so that a higher value is later in the job's life, which lets the
/// interface sort and group without a lookup table.
/// </remarks>
public enum DownloadPhase
{
    /// <summary>Waiting for a slot.</summary>
    Queued = 0,

    /// <summary>Held by the user until they resume it.</summary>
    Paused = 1,

    /// <summary>Working out where the game can actually be fetched from.</summary>
    Resolving = 2,

    /// <summary>Transferring.</summary>
    Downloading = 3,

    /// <summary>Checking the transferred bytes against the published digest.</summary>
    Verifying = 4,

    /// <summary>Unpacking an archive.</summary>
    Extracting = 5,

    /// <summary>Looking for something to launch in what was unpacked.</summary>
    Detecting = 6,

    /// <summary>Finished, and waiting for the user to confirm what to add.</summary>
    ReadyToInstall = 7,

    /// <summary>Added to the library.</summary>
    Completed = 8,

    /// <summary>Stopped by the user.</summary>
    Cancelled = 9,

    /// <summary>Stopped by a failure. Retryable.</summary>
    Failed = 10
}

/// <summary>
/// One download the queue is looking after.
/// </summary>
/// <remarks>
/// <para>
/// Mutable and observed by the interface, so progress is reported by updating
/// this rather than by raising an event per byte. The queue owns every write;
/// the view model only reads.
/// </para>
/// <para>
/// Deliberately not persisted. A queue that survived a restart would promise to
/// resume work whose temporary state — a part file, an extraction folder, a
/// half-written database row — may no longer be there. The <c>.part</c> file on
/// disk is what actually makes a download resumable, and re-queueing is one
/// click.
/// </para>
/// </remarks>
public sealed class DownloadJob
{
    /// <summary>Identifies this job for the lifetime of the process.</summary>
    public required string JobId { get; init; }

    /// <summary>The catalogue listing being installed.</summary>
    public required string ListingId { get; init; }

    /// <summary>Title shown in the queue.</summary>
    public required string Title { get; init; }

    /// <summary>Where the job has got to.</summary>
    public DownloadPhase Phase { get; set; } = DownloadPhase.Queued;

    /// <summary>Bytes transferred so far.</summary>
    public long BytesReceived { get; set; }

    /// <summary>Total expected size, or <see langword="null"/> when the server did not say.</summary>
    public long? TotalBytes { get; set; }

    /// <summary>Recent transfer rate in bytes per second.</summary>
    public double BytesPerSecond { get; set; }

    /// <summary>Time since the transfer began.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>
    /// Position in the queue; lower runs first.
    /// </summary>
    /// <remarks>
    /// A sparse integer rather than a list index, so reordering one item does not
    /// mean rewriting every other item's position.
    /// </remarks>
    public int Priority { get; set; }

    /// <summary>Which source supplied the address being used.</summary>
    public string? SourceKey { get; set; }

    /// <summary>
    /// A source the user asked to be tried first, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SourceKey"/>, which records where the file
    /// actually came from once a transfer has succeeded. This is a request made
    /// before anything was fetched, and the two disagree whenever the chosen
    /// source turned out to be unreachable and a later mirror answered instead.
    /// </remarks>
    public string? PreferredSourceKey { get; init; }

    /// <summary>How many mirrors have been tried.</summary>
    public int MirrorsTried { get; set; }

    /// <summary>Peers or servers connected.</summary>
    /// <remarks>
    /// Null when the engine does not report it — the built-in transport never
    /// does, and aria2 only does when its RPC interface answers. Distinguished
    /// from zero, which means connected to none.
    /// </remarks>
    public int? Peers { get; set; }

    /// <summary>Seeders connected, for a torrent transfer.</summary>
    /// <remarks>
    /// Null for an HTTP transfer. A torrent with peers but no seeders is a
    /// download that may never finish, which is worth being able to see.
    /// </remarks>
    public int? Seeders { get; set; }

    /// <summary>Where the finished download was unpacked, once it has been.</summary>
    public string? InstallDirectory { get; set; }

    /// <summary>Executables found in what was unpacked.</summary>
    public IReadOnlyList<DiscoveredGame> Candidates { get; set; } = [];

    /// <summary>What went wrong, when the job failed.</summary>
    public string? Error { get; set; }

    /// <summary>The most recent line of progress text.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>When the job was queued.</summary>
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Gets completion as a fraction, or <see langword="null"/> when the total is unknown.</summary>
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0d, 1d) : null;

    /// <summary>
    /// Gets the estimated time remaining, or <see langword="null"/> when it
    /// cannot be estimated.
    /// </summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (TotalBytes is not > 0 || BytesPerSecond <= 0 || Phase != DownloadPhase.Downloading)
            {
                return null;
            }

            var remaining = TotalBytes.Value - BytesReceived;

            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }

    /// <summary>Gets a value indicating whether the job has stopped for good.</summary>
    public bool IsTerminal =>
        Phase is DownloadPhase.Completed or DownloadPhase.Cancelled or DownloadPhase.Failed;

    /// <summary>Gets a value indicating whether the job is doing work right now.</summary>
    public bool IsActive =>
        Phase is DownloadPhase.Resolving or DownloadPhase.Downloading
            or DownloadPhase.Verifying or DownloadPhase.Extracting or DownloadPhase.Detecting;
}
