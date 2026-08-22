using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// Moves bytes with <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// The engine the launcher has always used, unchanged in behaviour and moved
/// behind <see cref="IDownloadTransport"/> so a second one can be added beside
/// it. It is always available — it needs nothing installed — which is what makes
/// it the fallback when an external engine is missing.
/// </para>
/// <para>
/// Resume is judged by the response status, never by <c>Accept-Ranges</c>. Some
/// servers advertise ranges and ignore them: a 206 continues the file, and a 200
/// means the whole thing is coming and the partial prefix must be discarded
/// rather than appended to.
/// </para>
/// </remarks>
public sealed class HttpDownloadTransport : IDownloadTransport
{
    private const int BufferSize = 128 * 1024;

    /// <summary>How long a transfer must be quiet before that is worth reporting.</summary>
    /// <remarks>
    /// Long enough that an ordinary gap between packets on a slow link does not
    /// make the row flicker between moving and stalled, short enough that a dead
    /// connection is visible before anyone reaches for the cancel button.
    /// </remarks>
    private static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromSeconds(5);

    /// <summary>How often the watchdog looks.</summary>
    private static readonly TimeSpan StallReportInterval = TimeSpan.FromSeconds(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpDownloadTransport> _logger;
    private readonly TimeSpan _stallThreshold;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the transfer client.</param>
    /// <param name="logger">Logger for transfer diagnostics.</param>
    /// <param name="stallThreshold">
    /// How long a transfer must be quiet before that is reported. Defaults to a
    /// value chosen so an ordinary gap between packets does not make the row
    /// flicker; a test shortens it rather than waiting out the real one.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public HttpDownloadTransport(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpDownloadTransport> logger,
        TimeSpan? stallThreshold = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stallThreshold = stallThreshold ?? DefaultStallThreshold;
    }

    /// <inheritdoc />
    public string Name => "HttpClient";

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.Http;

    /// <summary>
    /// Ranked last.
    /// </summary>
    /// <remarks>
    /// Not because it is bad, but because it is the floor: anything else that is
    /// installed and applicable is there because someone chose to install it.
    /// </remarks>
    public int Priority => 100;

    /// <inheritdoc />
    /// <remarks>Always. There is nothing to install and nothing to probe for.</remarks>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <inheritdoc />
    public async Task<TransportOutcome> TransferAsync(
        TransportRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Payload != DownloadPayload.Http)
        {
            throw new InvalidOperationException(
                $"{Name} cannot move a {request.Payload} payload.");
        }

        var client = _httpClientFactory.CreateClient(DownloadService.HttpClientName);

        var existingBytes = request.AllowResume && File.Exists(request.PartPath)
            ? new FileInfo(request.PartPath).Length
            : 0;

        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);

        if (existingBytes > 0)
        {
            message.Headers.Range = new RangeHeaderValue(existingBytes, null);
            _logger.LogInformation("Resuming {Url} from byte {Offset}.", request.Url, existingBytes);
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
            _logger.LogInformation("{Url} does not support resuming; restarting the transfer.", request.Url);
            existingBytes = 0;
        }

        var totalBytes = response.Content.Headers.ContentLength is { } length
            ? length + (resumed ? existingBytes : 0)
            : (long?)null;

        var transferred = await CopyToFileAsync(
            response, request.PartPath, resumed, existingBytes, totalBytes, progress,
            _stallThreshold, cancellationToken)
            .ConfigureAwait(false);

        return new TransportOutcome(request.PartPath, transferred, resumed);
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
    /// <param name="stallThreshold">How long a silence must last before it is reported.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>The number of bytes pulled over the network.</returns>
    private static async Task<long> CopyToFileAsync(
        HttpResponseMessage response,
        string partPath,
        bool append,
        long startingBytes,
        long? totalBytes,
        IProgress<DownloadProgress>? progress,
        TimeSpan stallThreshold,
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

        // A read that never returns reports nothing, and the loop below is the
        // only thing reporting — so a server that goes quiet used to leave the row
        // frozen on whatever it last said, with no way to tell a slow transfer
        // from a dead one. The watchdog reports on its own clock instead.
        using var stalling = new CancellationTokenSource();

        // Ticks rather than a TimeSpan: this is read from another thread, and
        // Volatile only handles primitives.
        var lastMovedTicks = 0L;

        var watchdog = progress is null
            ? Task.CompletedTask
            : WatchForStallAsync(
                () => (Volatile.Read(ref transferred),
                       TimeSpan.FromTicks(Volatile.Read(ref lastMovedTicks))),
                startingBytes,
                totalBytes,
                stopwatch,
                progress,
                stallThreshold,
                stalling.Token);

        try
        {

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

            Volatile.Write(ref lastMovedTicks, elapsed.Ticks);

            progress?.Report(new DownloadProgress(startingBytes + transferred, totalBytes, rate, elapsed));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new DownloadProgress(
            startingBytes + transferred, totalBytes, rate, stopwatch.Elapsed));

        return transferred;
        }
        finally
        {
            await stalling.CancelAsync().ConfigureAwait(false);

            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the watchdog is cancelled by design.
            }
        }
    }

    /// <summary>
    /// Reports a transfer that has gone quiet, on its own clock.
    /// </summary>
    /// <param name="sample">Reads the bytes so far and when they last moved.</param>
    /// <param name="startingBytes">Bytes already present when appending.</param>
    /// <param name="totalBytes">Expected final size, if known.</param>
    /// <param name="stopwatch">Running since the transfer began.</param>
    /// <param name="progress">Where to report.</param>
    /// <param name="stallThreshold">How long a silence must last before it is reported.</param>
    /// <param name="cancellationToken">Stops the watchdog when the copy ends.</param>
    /// <returns>A task that completes when the watchdog stops.</returns>
    /// <remarks>
    /// Reports nothing while data is arriving — the copy loop is already doing
    /// that, and two reporters describing the same moving transfer would only
    /// fight. It speaks up once the gap since the last byte is long enough to look
    /// like a fault rather than a pause between packets.
    /// </remarks>
    private static async Task WatchForStallAsync(
        Func<(long Transferred, TimeSpan LastMovedAt)> sample,
        long startingBytes,
        long? totalBytes,
        Stopwatch stopwatch,
        IProgress<DownloadProgress> progress,
        TimeSpan stallThreshold,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(StallReportInterval, cancellationToken).ConfigureAwait(false);

                var (transferred, lastMovedAt) = sample();
                var stalled = stopwatch.Elapsed - lastMovedAt;

                if (stalled < stallThreshold)
                {
                    continue;
                }

                // No StallLimit: this transport waits for as long as the request
                // itself allows rather than abandoning a quiet connection on a
                // deadline of its own. Saying how long it has been quiet is the
                // honest half of that, and it is the half that was missing.
                progress.Report(new DownloadProgress(
                    startingBytes + transferred, totalBytes, 0, stopwatch.Elapsed)
                {
                    StalledFor = stalled
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The copy finished.
        }
    }
}
