using System.Net.Http;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// Hash algorithms accepted for checksum verification.
/// </summary>
public enum ChecksumAlgorithm
{
    /// <summary>Infer the algorithm from the length of the supplied hex digest.</summary>
    Auto = 0,

    /// <summary>MD5, 32 hex characters. Offered only because publishers still print it.</summary>
    Md5 = 1,

    /// <summary>SHA-1, 40 hex characters.</summary>
    Sha1 = 2,

    /// <summary>SHA-256, 64 hex characters.</summary>
    Sha256 = 3,

    /// <summary>SHA-512, 128 hex characters.</summary>
    Sha512 = 4
}

/// <summary>
/// Describes a file to download.
/// </summary>
public sealed record DownloadRequest
{
    /// <summary>Direct URL of the file. Must be <c>http</c> or <c>https</c>.</summary>
    public required Uri Url { get; init; }

    /// <summary>Directory the file is written into. Created if missing.</summary>
    public required string DestinationDirectory { get; init; }

    /// <summary>
    /// File name to save as, or <see langword="null"/> to derive one from the
    /// response headers or the URL.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Expected hash as a hex string, or <see langword="null"/> to skip
    /// verification.
    /// </summary>
    public string? ExpectedChecksum { get; init; }

    /// <summary>Algorithm for <see cref="ExpectedChecksum"/>.</summary>
    public ChecksumAlgorithm ChecksumAlgorithm { get; init; } = ChecksumAlgorithm.Auto;

    /// <summary>
    /// Whether to attempt to continue a partial download already on disk.
    /// </summary>
    /// <remarks>
    /// Only a request; whether it happens depends on the server honouring a
    /// range request. A server that ignores it simply restarts the transfer.
    /// </remarks>
    public bool AllowResume { get; init; } = true;
}

/// <summary>
/// Progress of a running download.
/// </summary>
/// <param name="BytesReceived">Bytes written so far, including any resumed prefix.</param>
/// <param name="TotalBytes">Total expected size, or <see langword="null"/> when the server did not say.</param>
/// <param name="BytesPerSecond">Recent transfer rate.</param>
/// <param name="Elapsed">Time since the transfer started.</param>
public sealed record DownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan Elapsed)
{
    /// <summary>
    /// Peers or servers connected, or <see langword="null"/> when the transport
    /// does not report it.
    /// </summary>
    /// <remarks>
    /// Distinguished from zero, which means connected to none — a torrent that
    /// has found nobody yet is a thing worth being able to see.
    /// </remarks>
    public int? Peers { get; init; }

    /// <summary>
    /// Seeders connected, or <see langword="null"/> for anything that is not a
    /// torrent.
    /// </summary>
    public int? Seeders { get; init; }

    /// <summary>
    /// Gets a value indicating whether a torrent is still fetching its metadata.
    /// </summary>
    /// <remarks>
    /// The phase a magnet link starts in, where there is no total size, no
    /// progress and no rate because the file names are not known yet. Reported
    /// separately because it looks exactly like a stalled transfer otherwise,
    /// and the two want very different reactions from whoever is watching.
    /// </remarks>
    public bool ResolvingMetadata { get; init; }

    /// <summary>
    /// Gets how long nothing has been transferred, or <see langword="null"/> when
    /// something is moving or the transport does not track it.
    /// </summary>
    public TimeSpan? StalledFor { get; init; }

    /// <summary>
    /// Gets how long a stall is tolerated before the transport gives up, or
    /// <see langword="null"/> when it waits indefinitely.
    /// </summary>
    /// <remarks>
    /// Shown alongside <see cref="StalledFor"/> so a wait has a visible end. A
    /// progress line that says nothing is happening, without saying for how much
    /// longer it will keep not happening, is what makes people kill a transfer
    /// that was about to start.
    /// </remarks>
    public TimeSpan? StallLimit { get; init; }

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
            if (TotalBytes is not > 0 || BytesPerSecond <= 0)
            {
                return null;
            }

            var remaining = TotalBytes.Value - BytesReceived;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }
}

/// <summary>
/// The outcome of a completed download.
/// </summary>
/// <param name="FilePath">Absolute path to the downloaded file.</param>
/// <param name="TotalBytes">Size of the file on disk.</param>
/// <param name="BytesTransferred">Bytes actually pulled over the network this run.</param>
/// <param name="WasResumed">Whether an existing partial file was continued.</param>
/// <param name="ChecksumVerified">
/// <see langword="true"/> when a checksum was supplied and matched;
/// <see langword="false"/> when none was supplied.
/// </param>
public sealed record DownloadResult(
    string FilePath,
    long TotalBytes,
    long BytesTransferred,
    bool WasResumed,
    bool ChecksumVerified);

/// <summary>
/// Downloads a file from a direct URL.
/// </summary>
/// <remarks>
/// Deliberately generic. It understands HTTP — redirects, ranges, content
/// disposition — and nothing about any particular host. There is no
/// site-specific handling, no page scraping and no link extraction: the caller
/// supplies a URL that already points at a file.
/// </remarks>
public interface IDownloadService
{
    /// <summary>
    /// Downloads a file, resuming a partial transfer where the server allows it.
    /// </summary>
    /// <param name="request">What to download and where to put it.</param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the transfer, leaving the partial file for a later resume.</param>
    /// <returns>Details of the completed download.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The URL is not an absolute http or https address.</exception>
    /// <exception cref="HttpRequestException">The server returned an error or the transfer failed.</exception>
    /// <exception cref="InvalidOperationException">A checksum was supplied and did not match.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the transfer.</exception>
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
