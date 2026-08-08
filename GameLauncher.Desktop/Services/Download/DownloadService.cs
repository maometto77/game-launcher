using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// Default <see cref="IDownloadService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Owns the rules around a transfer and delegates the transfer itself to an
/// <see cref="IDownloadTransport"/>. Validating the address, choosing a file
/// name, verifying the checksum and renaming into place all stay here, in one
/// implementation, so a second engine cannot quietly get any of them wrong.
/// </para>
/// <para>
/// The transfer writes to a <c>.part</c> file and it is renamed only once the
/// bytes are complete and any checksum has matched. A file that appears at the
/// final path is therefore always whole, and an interrupted transfer leaves
/// something obviously unfinished that a later run can continue.
/// </para>
/// <para>
/// Transports are chosen by capability and availability, best priority first, so
/// an engine that is not installed is simply not selected and the built-in
/// <see cref="HttpDownloadTransport"/> takes over.
/// </para>
/// </remarks>
public sealed class DownloadService : IDownloadService
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for transfers.</summary>
    public const string HttpClientName = "downloads";

    /// <summary>Read buffer used when hashing a completed file.</summary>
    private const int BufferSize = 128 * 1024;

    private readonly IReadOnlyList<IDownloadTransport> _transports;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DownloadService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="transports">The engines available to move bytes.</param>
    /// <param name="httpClientFactory">Supplies the client used to probe for a file name.</param>
    /// <param name="logger">Logger for transfer diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No transport is registered.</exception>
    public DownloadService(
        IEnumerable<IDownloadTransport> transports,
        IHttpClientFactory httpClientFactory,
        ILogger<DownloadService> logger)
    {
        ArgumentNullException.ThrowIfNull(transports);

        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _transports = transports.OrderBy(transport => transport.Priority).ToArray();

        if (_transports.Count == 0)
        {
            throw new InvalidOperationException("At least one download transport must be registered.");
        }
    }

    /// <inheritdoc />
    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = ClassifyPayload(request.Url);

        ValidateUrl(request.Url, payload);

        Directory.CreateDirectory(request.DestinationDirectory);

        var transport = await SelectTransportAsync(payload, cancellationToken).ConfigureAwait(false);

        return payload == DownloadPayload.Torrent
            ? await DownloadTorrentAsync(request, transport, progress, cancellationToken).ConfigureAwait(false)
            : await DownloadFileAsync(request, transport, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a single file.
    /// </summary>
    /// <param name="request">What to download.</param>
    /// <param name="transport">The engine to move it with.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>Details of the completed download.</returns>
    private async Task<DownloadResult> DownloadFileAsync(
        DownloadRequest request,
        IDownloadTransport transport,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var fileName = request.FileName is { Length: > 0 }
            ? SanitiseFileName(request.FileName)
            : await ResolveFileNameAsync(client, request.Url, cancellationToken).ConfigureAwait(false);

        var finalPath = Path.Combine(request.DestinationDirectory, fileName);
        var partPath = finalPath + ".part";

        var outcome = await transport.TransferAsync(
            new TransportRequest
            {
                Url = request.Url,
                Payload = DownloadPayload.Http,
                PartPath = partPath,
                DestinationDirectory = request.DestinationDirectory,
                AllowResume = request.AllowResume
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        var checksumVerified = await VerifyOrDeleteAsync(partPath, request, cancellationToken)
            .ConfigureAwait(false);

        // Rename last, so the final path only ever holds a complete, verified file.
        File.Move(partPath, finalPath, overwrite: true);

        var finalSize = new FileInfo(finalPath).Length;

        _logger.LogInformation(
            "Downloaded {Url} to {Path} via {Transport} ({Bytes} bytes, resumed={Resumed}, checksum={Checksum}).",
            request.Url, finalPath, transport.Name, finalSize, outcome.WasResumed, checksumVerified);

        return new DownloadResult(
            finalPath, finalSize, outcome.BytesTransferred, outcome.WasResumed, checksumVerified);
    }

    /// <summary>
    /// Downloads a BitTorrent payload.
    /// </summary>
    /// <param name="request">What to download.</param>
    /// <param name="transport">The engine to move it with.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>Details of the completed download.</returns>
    /// <remarks>
    /// A torrent names its own contents and may produce a directory, so there is
    /// no file name to negotiate and nothing to rename — the transport reports
    /// where the payload landed. BitTorrent verifies every piece against the
    /// metadata as it downloads, so a supplied checksum is a second opinion
    /// rather than the only one; it is still honoured for a single file.
    /// </remarks>
    private async Task<DownloadResult> DownloadTorrentAsync(
        DownloadRequest request,
        IDownloadTransport transport,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var outcome = await transport.TransferAsync(
            new TransportRequest
            {
                Url = request.Url,
                Payload = DownloadPayload.Torrent,
                PartPath = Path.Combine(request.DestinationDirectory, "torrent.part"),
                DestinationDirectory = request.DestinationDirectory,
                AllowResume = request.AllowResume
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        var checksumVerified = false;

        if (!outcome.IsDirectory)
        {
            checksumVerified = await VerifyOrDeleteAsync(outcome.ProducedPath, request, cancellationToken)
                .ConfigureAwait(false);
        }

        var size = outcome.IsDirectory
            ? DirectorySize(outcome.ProducedPath)
            : new FileInfo(outcome.ProducedPath).Length;

        _logger.LogInformation(
            "Fetched torrent {Url} to {Path} via {Transport} ({Bytes} bytes).",
            request.Url, outcome.ProducedPath, transport.Name, size);

        return new DownloadResult(
            outcome.ProducedPath, size, outcome.BytesTransferred, outcome.WasResumed, checksumVerified);
    }

    /// <summary>
    /// Verifies a downloaded file, deleting it when it does not match.
    /// </summary>
    /// <param name="path">The file to check.</param>
    /// <param name="request">The request carrying the expected digest.</param>
    /// <param name="cancellationToken">Cancels hashing.</param>
    /// <returns><see langword="true"/> when a checksum was supplied and matched.</returns>
    /// <exception cref="InvalidOperationException">The checksum did not match.</exception>
    private async Task<bool> VerifyOrDeleteAsync(
        string path,
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedChecksum))
        {
            return false;
        }

        var verified = await VerifyChecksumAsync(
            path, request.ExpectedChecksum, request.ChecksumAlgorithm, cancellationToken)
            .ConfigureAwait(false);

        if (verified)
        {
            return true;
        }

        // Deleted rather than kept: a corrupt file that stays on disk would be
        // resumed next time, and resuming corruption never converges.
        TryDelete(path);

        throw new InvalidOperationException(
            "The downloaded file did not match the checksum supplied. It has been deleted.");
    }

    /// <summary>
    /// Picks the best available transport for a payload.
    /// </summary>
    /// <param name="payload">What is being moved.</param>
    /// <param name="cancellationToken">Cancels the availability checks.</param>
    /// <returns>The chosen transport.</returns>
    /// <exception cref="InvalidOperationException">Nothing registered can move this payload.</exception>
    private async Task<IDownloadTransport> SelectTransportAsync(
        DownloadPayload payload,
        CancellationToken cancellationToken)
    {
        var required = payload == DownloadPayload.Torrent
            ? TransportCapabilities.Torrent
            : TransportCapabilities.Http;

        foreach (var candidate in _transports.Where(transport => transport.Capabilities.HasFlag(required)))
        {
            if (await candidate.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }

            _logger.LogDebug("{Transport} is not available; trying the next one.", candidate.Name);
        }

        throw new InvalidOperationException(
            payload == DownloadPayload.Torrent
                ? "Torrent downloads need aria2c, which was not found. Install it, or use a direct HTTP mirror."
                : "No download transport is available.");
    }

    /// <summary>
    /// Works out what an address points at.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <returns>The payload kind.</returns>
    /// <remarks>
    /// Inferred rather than declared, so every existing caller keeps working
    /// unchanged: a <c>magnet:</c> link or a <c>.torrent</c> file is a torrent,
    /// and anything else is an ordinary file.
    /// </remarks>
    internal static DownloadPayload ClassifyPayload(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri)
        {
            return DownloadPayload.Http;
        }

        if (string.Equals(url.Scheme, "magnet", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadPayload.Torrent;
        }

        return url.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            ? DownloadPayload.Torrent
            : DownloadPayload.Http;
    }

    /// <summary>Adds up the size of everything under a directory.</summary>
    /// <param name="path">The directory to measure.</param>
    /// <returns>Total size in bytes, or zero when it does not exist.</returns>
    private static long DirectorySize(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length)
            : 0;

    /// <summary>
    /// Rejects anything that is not an absolute http or https URL.
    /// </summary>
    /// <param name="url">The URL to check.</param>
    /// <param name="payload">What the address was classified as.</param>
    /// <exception cref="ArgumentException">The URL is unusable or uses another scheme.</exception>
    /// <remarks>
    /// Blocks <c>file://</c> in particular: a downloader that accepts it turns a
    /// pasted string into an arbitrary local file copy.
    /// </remarks>
    private static void ValidateUrl(Uri url, DownloadPayload payload)
    {
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("The download address must be an absolute URL.", nameof(url));
        }

        // Magnet is allowed only now that a torrent-capable transport exists, and
        // only for an address already classified as one.
        if (payload == DownloadPayload.Torrent &&
            string.Equals(url.Scheme, "magnet", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"Only http, https and magnet downloads are supported, but the address uses '{url.Scheme}'.",
                nameof(url));
        }
    }

    /// <summary>
    /// Works out what to call the downloaded file.
    /// </summary>
    /// <param name="client">Client used for the probe request.</param>
    /// <param name="url">The download address.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A safe file name.</returns>
    /// <remarks>
    /// Prefers the server's <c>Content-Disposition</c>, falls back to the last
    /// path segment, and finally to a generic name. A HEAD request is used for
    /// the probe and its failure is ignored, because plenty of servers reject
    /// HEAD while serving GET perfectly well.
    /// </remarks>
    private async Task<string> ResolveFileNameAsync(
        HttpClient client,
        Uri url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(head, cancellationToken).ConfigureAwait(false);

            var disposition = response.Content.Headers.ContentDisposition;
            var suggested = disposition?.FileNameStar ?? disposition?.FileName;

            if (!string.IsNullOrWhiteSpace(suggested))
            {
                return SanitiseFileName(suggested.Trim('"'));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "HEAD probe for {Url} failed; falling back to the URL path.", url);
        }

        var fromPath = Path.GetFileName(url.AbsolutePath);
        return SanitiseFileName(Uri.UnescapeDataString(fromPath));
    }

    /// <summary>
    /// Reduces a server-supplied name to something safe to write.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <returns>A bare file name with no directory component.</returns>
    /// <remarks>
    /// A <c>Content-Disposition</c> header is attacker-controlled for any URL the
    /// user did not personally vet, so the name is stripped to its final segment
    /// and cleaned of invalid characters. Without that, a name of
    /// <c>..\..\Startup\evil.exe</c> would escape the download folder.
    /// </remarks>
    internal static string SanitiseFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "download.bin";
        }

        // Any directory component at all is discarded, on either separator.
        var name = value.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            name = name[(lastSlash + 1)..];
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var result = builder.ToString().Trim('.', ' ');

        if (result.Length > 150)
        {
            result = result[..150];
        }

        return string.IsNullOrWhiteSpace(result) ? "download.bin" : result;
    }

    /// <summary>
    /// Hashes the downloaded file and compares it with the expected digest.
    /// </summary>
    /// <param name="path">File to hash.</param>
    /// <param name="expected">Expected digest as hex.</param>
    /// <param name="algorithm">Algorithm to use, or auto-detect from the digest length.</param>
    /// <param name="cancellationToken">Cancels hashing.</param>
    /// <returns><see langword="true"/> when the digests match.</returns>
    /// <remarks>
    /// Computed after the transfer rather than incrementally, because a resumed
    /// download never sees the bytes that were already on disk. One extra read of
    /// the file is a small price for a check that is correct in both cases.
    /// </remarks>
    private async Task<bool> VerifyChecksumAsync(
        string path,
        string expected,
        ChecksumAlgorithm algorithm,
        CancellationToken cancellationToken)
    {
        var normalised = expected.Trim().Replace("-", string.Empty).ToLowerInvariant();
        var resolved = algorithm == ChecksumAlgorithm.Auto ? Detect(normalised) : algorithm;

        if (resolved == ChecksumAlgorithm.Auto)
        {
            throw new InvalidOperationException(
                $"A {normalised.Length}-character checksum does not match any supported algorithm.");
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        using HashAlgorithm hasher = resolved switch
        {
            ChecksumAlgorithm.Md5 => MD5.Create(),
            ChecksumAlgorithm.Sha1 => SHA1.Create(),
            ChecksumAlgorithm.Sha512 => SHA512.Create(),
            _ => SHA256.Create()
        };

        var hash = await hasher.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        var matched = string.Equals(actual, normalised, StringComparison.Ordinal);

        if (!matched)
        {
            _logger.LogWarning(
                "Checksum mismatch for {Path}: expected {Expected}, computed {Actual}.",
                path, normalised, actual);
        }

        return matched;
    }

    /// <summary>Infers the hash algorithm from a digest's length.</summary>
    /// <param name="digest">Normalised hex digest.</param>
    /// <returns>The matching algorithm, or <see cref="ChecksumAlgorithm.Auto"/> when the length is unrecognised.</returns>
    private static ChecksumAlgorithm Detect(string digest) => digest.Length switch
    {
        32 => ChecksumAlgorithm.Md5,
        40 => ChecksumAlgorithm.Sha1,
        64 => ChecksumAlgorithm.Sha256,
        128 => ChecksumAlgorithm.Sha512,
        _ => ChecksumAlgorithm.Auto
    };

    /// <summary>Deletes a file, ignoring failures.</summary>
    /// <param name="path">File to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do; the caller is already reporting a failure.
        }
    }
}
