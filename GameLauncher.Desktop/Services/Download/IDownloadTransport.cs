namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// What kind of payload an address points at.
/// </summary>
public enum DownloadPayload
{
    /// <summary>A file served over HTTP.</summary>
    Http = 0,

    /// <summary>
    /// A BitTorrent payload: a <c>magnet:</c> link or a <c>.torrent</c> file.
    /// </summary>
    Torrent = 1
}

/// <summary>
/// What a transport is able to move.
/// </summary>
[Flags]
public enum TransportCapabilities
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Files served over HTTP or HTTPS.</summary>
    Http = 1,

    /// <summary>BitTorrent payloads.</summary>
    Torrent = 2
}

/// <summary>
/// One transfer for a transport to perform.
/// </summary>
public sealed record TransportRequest
{
    /// <summary>Address to fetch.</summary>
    public required Uri Url { get; init; }

    /// <summary>What the address points at.</summary>
    public DownloadPayload Payload { get; init; } = DownloadPayload.Http;

    /// <summary>
    /// Where to write, for an HTTP transfer.
    /// </summary>
    /// <remarks>
    /// The in-progress <c>.part</c> path, not the final one. A transport never
    /// writes to the destination the caller will read from — that rename is the
    /// download service's, and only after the checksum passes.
    /// </remarks>
    public required string PartPath { get; init; }

    /// <summary>
    /// Directory a torrent transfer writes into.
    /// </summary>
    /// <remarks>
    /// A torrent names its own contents and may produce a directory rather than
    /// one file, so a destination path cannot be imposed on it in advance.
    /// </remarks>
    public required string DestinationDirectory { get; init; }

    /// <summary>Whether to continue an existing partial transfer.</summary>
    public bool AllowResume { get; init; } = true;
}

/// <summary>
/// What a transport produced.
/// </summary>
/// <param name="ProducedPath">
/// Where the bytes actually landed. Equal to the request's part path for an HTTP
/// transfer; whatever the payload named itself for a torrent.
/// </param>
/// <param name="BytesTransferred">Bytes pulled over the network this run.</param>
/// <param name="WasResumed">Whether an existing partial transfer was continued.</param>
/// <param name="IsDirectory">Whether the produced path is a directory.</param>
public sealed record TransportOutcome(
    string ProducedPath,
    long BytesTransferred,
    bool WasResumed,
    bool IsDirectory = false);

/// <summary>
/// Moves bytes for <see cref="IDownloadService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The seam that makes the download engine swappable. A transport is responsible
/// for the transfer and nothing else: the surrounding rules — validating the
/// address, choosing a file name, verifying the checksum, and only then renaming
/// the finished file into place — stay in one implementation, so they cannot
/// drift apart between engines or be quietly forgotten by a new one.
/// </para>
/// <para>
/// Transports are registered as an open set and chosen by capability and
/// availability, so an engine that is not installed simply is not selected.
/// </para>
/// </remarks>
public interface IDownloadTransport
{
    /// <summary>Human-readable name, used when reporting which engine ran.</summary>
    string Name { get; }

    /// <summary>What this transport can move.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Preference among transports that could both do the job; lower wins.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Determines whether the transport can run right now.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when it is usable.</returns>
    /// <remarks>
    /// An external engine may not be installed. The answer is expected to be
    /// cached by the implementation: probing for a binary on every download would
    /// cost a process launch per file.
    /// </remarks>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs one transfer.
    /// </summary>
    /// <param name="request">What to move and where.</param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the transfer, leaving any partial file in place.</param>
    /// <returns>What was produced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">The transfer failed.</exception>
    /// <exception cref="InvalidOperationException">The transport could not run.</exception>
    Task<TransportOutcome> TransferAsync(
        TransportRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
