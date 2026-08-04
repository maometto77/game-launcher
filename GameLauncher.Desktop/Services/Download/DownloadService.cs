using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// Default <see cref="IDownloadService"/>, built on <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// The transfer writes to a <c>.part</c> file and renames it only once the bytes
/// are complete and any checksum has matched. A file that appears at the final
/// path is therefore always whole, and an interrupted transfer leaves something
/// obviously unfinished that a later run can continue.
/// </para>
/// <para>
/// Resume is attempted with a range request rather than by trusting
/// <c>Accept-Ranges</c>: some servers advertise it and then ignore it, so the
/// response status is the only reliable signal. A <c>206</c> continues the file;
/// a <c>200</c> means the server sent the whole thing regardless, and the partial
/// file is discarded.
/// </para>
/// </remarks>
public sealed class DownloadService : IDownloadService
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for transfers.</summary>
    public const string HttpClientName = "downloads";

    private const int BufferSize = 128 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DownloadService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured transfer client.</param>
    /// <param name="logger">Logger for transfer diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DownloadService(IHttpClientFactory httpClientFactory, ILogger<DownloadService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUrl(request.Url);

        Directory.CreateDirectory(request.DestinationDirectory);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        var fileName = request.FileName is { Length: > 0 }
            ? SanitiseFileName(request.FileName)
            : await ResolveFileNameAsync(client, request.Url, cancellationToken).ConfigureAwait(false);

        var finalPath = Path.Combine(request.DestinationDirectory, fileName);
        var partPath = finalPath + ".part";

        var existingBytes = request.AllowResume && File.Exists(partPath)
            ? new FileInfo(partPath).Length
            : 0;

        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);

        if (existingBytes > 0)
        {
            message.Headers.Range = new RangeHeaderValue(existingBytes, null);
            _logger.LogInformation(
                "Resuming {Url} from byte {Offset}.", request.Url, existingBytes);
        }

        using var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // The server ignored the range and is sending the whole file, so the
        // partial prefix is meaningless and must not be appended to.
        var resumed = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        if (existingBytes > 0 && !resumed)
        {
            _logger.LogInformation(
                "{Url} does not support resuming; restarting the transfer.", request.Url);
            existingBytes = 0;
        }

        var totalBytes = response.Content.Headers.ContentLength is { } length
            ? length + (resumed ? existingBytes : 0)
            : (long?)null;

        var transferred = await CopyToFileAsync(
            response, partPath, resumed, existingBytes, totalBytes, progress, cancellationToken)
            .ConfigureAwait(false);

        var checksumVerified = false;
        if (!string.IsNullOrWhiteSpace(request.ExpectedChecksum))
        {
            checksumVerified = await VerifyChecksumAsync(
                partPath, request.ExpectedChecksum, request.ChecksumAlgorithm, cancellationToken)
                .ConfigureAwait(false);

            if (!checksumVerified)
            {
                // Deleted rather than kept: a corrupt file that stays on disk would
                // be resumed next time, and resuming corruption never converges.
                TryDelete(partPath);

                throw new InvalidOperationException(
                    "The downloaded file did not match the checksum supplied. It has been deleted.");
            }
        }

        // Rename last, so the final path only ever holds a complete, verified file.
        File.Move(partPath, finalPath, overwrite: true);

        var finalSize = new FileInfo(finalPath).Length;

        _logger.LogInformation(
            "Downloaded {Url} to {Path} ({Bytes} bytes, resumed={Resumed}, checksum={Checksum}).",
            request.Url, finalPath, finalSize, resumed, checksumVerified);

        return new DownloadResult(finalPath, finalSize, transferred, resumed, checksumVerified);
    }

    /// <summary>
    /// Streams the response body to disk, reporting progress as it goes.
    /// </summary>
    /// <param name="response">The response whose body is being read.</param>
    /// <param name="partPath">Path of the in-progress file.</param>
    /// <param name="append">Whether to append to an existing partial file.</param>
    /// <param name="startingBytes">Bytes already present when appending.</param>
    /// <param name="totalBytes">Expected final size, if known.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>The number of bytes pulled over the network.</returns>
    private static async Task<long> CopyToFileAsync(
        HttpResponseMessage response,
        string partPath,
        bool append,
        long startingBytes,
        long? totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var destination = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        var buffer = new byte[BufferSize];
        var stopwatch = Stopwatch.StartNew();

        long transferred = 0;
        var lastReportAt = TimeSpan.Zero;
        var lastReportBytes = 0L;
        var rate = 0d;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            transferred += read;

            var elapsed = stopwatch.Elapsed;
            var sinceLastReport = elapsed - lastReportAt;

            // Throttled to a few updates a second. Reporting every buffer would
            // flood the dispatcher and make the UI slower than the download.
            if (sinceLastReport < TimeSpan.FromMilliseconds(200))
            {
                continue;
            }

            var instantaneous = (transferred - lastReportBytes) / sinceLastReport.TotalSeconds;

            // Smoothed, because raw per-interval rates jitter enough to make the
            // remaining-time estimate jump around unusably.
            rate = rate <= 0 ? instantaneous : (rate * 0.7) + (instantaneous * 0.3);

            lastReportAt = elapsed;
            lastReportBytes = transferred;

            progress?.Report(new DownloadProgress(startingBytes + transferred, totalBytes, rate, elapsed));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new DownloadProgress(
            startingBytes + transferred, totalBytes, rate, stopwatch.Elapsed));

        return transferred;
    }

    /// <summary>
    /// Rejects anything that is not an absolute http or https URL.
    /// </summary>
    /// <param name="url">The URL to check.</param>
    /// <exception cref="ArgumentException">The URL is unusable or uses another scheme.</exception>
    /// <remarks>
    /// Blocks <c>file://</c> in particular: a downloader that accepts it turns a
    /// pasted string into an arbitrary local file copy.
    /// </remarks>
    private static void ValidateUrl(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("The download address must be an absolute URL.", nameof(url));
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"Only http and https downloads are supported, but the address uses '{url.Scheme}'.",
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
