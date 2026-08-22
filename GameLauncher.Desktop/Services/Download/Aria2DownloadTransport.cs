using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// Moves bytes with <c>aria2c</c>.
/// </summary>
/// <remarks>
/// <para>
/// Buys two things the built-in engine cannot: several connections per file,
/// which matters because a single stream is limited by per-connection shaping
/// rather than by available bandwidth; and BitTorrent, which is the only way to
/// use the <c>.torrent</c> files the Internet Archive publishes for its items.
/// </para>
/// <para>
/// Each transfer is its own aria2c, started on the command line and left to exit
/// when it finishes. It is asked to open a JSON-RPC interface while it runs, on a
/// loopback port with a fresh secret, and that is where progress comes from:
/// bytes done, total size, current rate, and — for a torrent — how many peers and
/// seeders it is talking to. None of that can be worked out by watching a file
/// grow, and the total in particular is what turns an indeterminate bar into a
/// real one.
/// </para>
/// <para>
/// A daemon shared across the application was the alternative and is not what
/// this does. A process per transfer means no port held while nothing is
/// downloading, no lifetime to manage when the window closes, and no way for one
/// download to interfere with another — the process is already bound to the work
/// it was started for.
/// </para>
/// <para>
/// The transfer never depends on the RPC interface. Statistics are a poll on the
/// side; if the port cannot be bound or the calls fail, this falls back to
/// measuring the file on disk, exactly as it did before, and the download
/// completes either way.
/// </para>
/// <para>
/// If <c>aria2c</c> is missing, disabled, or cannot be started, this transport
/// steps aside and the download service falls back to
/// <see cref="HttpDownloadTransport"/>, so nothing depends on it being there.
/// </para>
/// </remarks>
public sealed class Aria2DownloadTransport : IDownloadTransport, IDisposable
{
    /// <summary>How often progress is sampled.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a transfer must be quiet before that is worth reporting.</summary>
    /// <remarks>
    /// Long enough that a gap between two pieces does not make the row flicker
    /// between moving and stalled, short enough to be visible before anyone
    /// reaches for the cancel button.
    /// </remarks>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5);

    /// <summary>How long to let aria2 shut itself down before it is killed.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a torrent may transfer nothing before aria2 abandons it.
    /// </summary>
    /// <remarks>
    /// Passed to aria2 as <c>--bt-stop-timeout</c> and reported alongside
    /// progress, so a transfer that has found no peers shows how long it will
    /// keep trying rather than looking frozen indefinitely.
    /// </remarks>
    private static readonly TimeSpan TorrentStallLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the RPC interface is given to answer before it is given up on.
    /// </summary>
    /// <remarks>
    /// Measured from the start of the transfer rather than counted in attempts,
    /// because a failed attempt is not a fixed cost: Windows takes about two
    /// seconds to report a refused loopback connection, so six attempts is
    /// fifteen seconds, not three. A deadline is the same length of patience
    /// whatever the failure costs.
    /// </remarks>
    private static readonly TimeSpan RpcGrace = TimeSpan.FromSeconds(5);

    private readonly ISettingsService _settings;
    private readonly IExternalToolLocator _tools;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DownloadHelperRegistry _helpers;
    private readonly ILogger<Aria2DownloadTransport> _logger;

    /// <summary>Every aria2c this transport has started and not yet seen exit.</summary>
    /// <remarks>
    /// Kept so closing the launcher does not leave one behind. Cancelling a
    /// transfer asks its process to stop, but the cancellation and the process's
    /// death are separate events, and an application that has already exited is
    /// no longer around to see the second one. A child process on Windows is not
    /// ended by its parent going away.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Process, byte> _live = new();

    /// <summary>
    /// Holds every aria2c this launcher starts, so the operating system kills
    /// them if this process dies without getting to.
    /// </summary>
    /// <remarks>
    /// <see cref="Dispose"/> covers a graceful exit and nothing else. A launcher
    /// killed to free a locked file during a rebuild, stopped from an IDE, or
    /// lost to a crash never runs it — and every one of those used to leave an
    /// aria2c downloading invisibly, writing into files the next launch would
    /// then fight over.
    /// </remarks>
    private readonly ChildProcessJob _job = new();

    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="settings">Supplies whether aria2 is enabled and where it lives.</param>
    /// <param name="tools">Finds the aria2c executable.</param>
    /// <param name="httpClientFactory">Supplies the client used for RPC calls.</param>
    /// <param name="helpers">Records started helpers so an unclean exit can be cleaned up after.</param>
    /// <param name="logger">Logger for transfer diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Aria2DownloadTransport(
        ISettingsService settings,
        IExternalToolLocator tools,
        IHttpClientFactory httpClientFactory,
        DownloadHelperRegistry helpers,
        ILogger<Aria2DownloadTransport> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _helpers = helpers ?? throw new ArgumentNullException(nameof(helpers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "aria2c";

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.Http | TransportCapabilities.Torrent;

    /// <summary>Preferred when it is available.</summary>
    public int Priority => 0;

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Current.Aria2Enabled)
        {
            return false;
        }

        return await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async Task<TransportOutcome> TransferAsync(
        TransportRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new TransportUnavailableException("aria2c is not available.");

        return request.Payload == DownloadPayload.Torrent
            ? await TransferTorrentAsync(executable, request, progress, cancellationToken).ConfigureAwait(false)
            : await TransferFileAsync(executable, request, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches a single file over HTTP with several connections.
    /// </summary>
    /// <param name="executable">Resolved path to aria2c.</param>
    /// <param name="request">What to move.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>What was produced.</returns>
    private async Task<TransportOutcome> TransferFileAsync(
        string executable,
        TransportRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(request.PartPath)
                        ?? request.DestinationDirectory;

        var fileName = Path.GetFileName(request.PartPath);

        var existingBytes = request.AllowResume && File.Exists(request.PartPath)
            ? new FileInfo(request.PartPath).Length
            : 0;

        // Without a control file aria2 cannot know which pieces of an existing
        // file are actually present, so it would restart anyway. Saying so up
        // front keeps the reported "resumed" flag honest.
        var resumable = request.AllowResume && File.Exists(request.PartPath + ".aria2");

        if (existingBytes > 0 && !resumable)
        {
            TryDelete(request.PartPath);
            existingBytes = 0;
        }

        var connections = Math.Clamp(_settings.Current.Aria2Connections, 1, 16);

        var arguments = new List<string>
        {
            "--dir=" + directory,
            "--out=" + fileName,
            "--continue=true",
            "--auto-file-renaming=false",
            "--allow-overwrite=true",

            // Several connections to one server is the entire point; the split
            // count is bounded because the Archive asks clients not to hammer it.
            "--split=" + connections.ToString(CultureInfo.InvariantCulture),
            "--max-connection-per-server=" + connections.ToString(CultureInfo.InvariantCulture),
            "--min-split-size=1M",
            "--console-log-level=warn",
            "--summary-interval=0",
            "--show-console-readout=false",
            "--user-agent=GameLauncher/1.0",
            request.Url.AbsoluteUri
        };

        await RunAsync(executable, arguments, request.PartPath, progress, cancellationToken)
            .ConfigureAwait(false);

        // aria2 leaves its control file behind on success; it is noise beside a
        // finished download and would confuse a later resume attempt.
        TryDelete(request.PartPath + ".aria2");

        var finalSize = File.Exists(request.PartPath) ? new FileInfo(request.PartPath).Length : 0;

        return new TransportOutcome(
            request.PartPath,
            Math.Max(0, finalSize - existingBytes),
            existingBytes > 0);
    }

    /// <summary>
    /// Fetches a BitTorrent payload.
    /// </summary>
    /// <param name="executable">Resolved path to aria2c.</param>
    /// <param name="request">What to move.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>What was produced.</returns>
    /// <remarks>
    /// A torrent names its own contents, so the payload is identified by what
    /// appeared in the destination directory rather than by a path imposed on it
    /// beforehand.
    /// </remarks>
    private async Task<TransportOutcome> TransferTorrentAsync(
        string executable,
        TransportRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.DestinationDirectory);

        var before = SnapshotEntries(request.DestinationDirectory);

        var arguments = new List<string>
        {
            "--dir=" + request.DestinationDirectory,
            "--continue=true",
            "--console-log-level=warn",
            "--summary-interval=0",
            "--show-console-readout=false",

            // Stop as soon as the download completes rather than seeding
            // indefinitely: this is a launcher, not a torrent client, and a
            // process that never exits would look like a hung download.
            "--seed-time=0",
            "--bt-stop-timeout=" +
                ((int)TorrentStallLimit.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            "--follow-torrent=mem",
            request.Url.AbsoluteUri
        };

        await RunAsync(
                executable, arguments, partPath: null, progress, cancellationToken, TorrentStallLimit)
            .ConfigureAwait(false);

        var produced = SnapshotEntries(request.DestinationDirectory)
            .Except(before, StringComparer.OrdinalIgnoreCase)
            .Where(entry => !entry.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (produced.Length == 0)
        {
            throw new InvalidOperationException(
                "aria2c reported success but the torrent produced no files.");
        }

        // A multi-file torrent unpacks into one directory, which is what should
        // be reported rather than an arbitrary file inside it.
        var payload = produced.FirstOrDefault(Directory.Exists) ?? produced[0];
        var isDirectory = Directory.Exists(payload);

        var size = isDirectory
            ? new DirectoryInfo(payload).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : new FileInfo(payload).Length;

        return new TransportOutcome(payload, size, WasResumed: false, IsDirectory: isDirectory);
    }

    /// <summary>
    /// Runs aria2c and waits for it, reporting progress from the file on disk.
    /// </summary>
    /// <param name="executable">Resolved path to aria2c.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="partPath">File to measure for progress, or <see langword="null"/>.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancels the transfer and kills the process.</param>
    /// <param name="stallLimit">
    /// How long the transfer may move nothing before aria2 abandons it, or
    /// <see langword="null"/> when it has no such deadline.
    /// </param>
    /// <exception cref="InvalidOperationException">aria2c exited with a failure code.</exception>
    private async Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? partPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? stallLimit = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // The statistics channel. Opened per transfer on a loopback port with a
        // fresh secret, and gone when the process is. A session that cannot be
        // created at all is not fatal: progress falls back to the file.
        var session = TryCreateSession();

        if (session is not null)
        {
            info.ArgumentList.Add("--enable-rpc=true");
            info.ArgumentList.Add("--rpc-listen-port=" + session.Port.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--rpc-secret=" + session.Secret);

            // Loopback only. This interface can start and stop downloads, and
            // there is no reason for anything off this machine to reach it.
            info.ArgumentList.Add("--rpc-listen-all=false");
            info.ArgumentList.Add("--rpc-allow-origin-all=false");
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        // Collected so a failure can say what aria2 actually complained about
        // rather than only that it exited non-zero.
        var errors = new List<string>();

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (errors)
                {
                    errors.Add(e.Data);
                }
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new TransportUnavailableException("aria2c could not be started.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ObjectDisposedException
                                       or InvalidOperationException or PlatformNotSupportedException)
        {
            // It answered --version at probe time and will not start now: it has
            // been moved, or something is blocking it. Reported as this transport
            // stepping aside rather than as a failed download, so the built-in
            // engine gets a turn.
            throw new TransportUnavailableException($"aria2c could not be started: {ex.Message}", ex);
        }

        _live[process] = 0;

        // Recorded before anything else can go wrong, so a launcher killed in the
        // next instant still leaves a note about what it started.
        _helpers.Register(process);

        if (!_job.Assign(process) && _job.IsEnforced)
        {
            _logger.LogDebug(
                "aria2c {Pid} could not be placed under this launcher's job object; " +
                "it will be stopped on a clean exit but could survive a kill.",
                process.Id);
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var rpc = session is null
            ? null
            : new Aria2RpcClient(_httpClientFactory.CreateClient(Aria2RpcClient.HttpClientName), session);

        var stopwatch = Stopwatch.StartNew();
        using var reporting = new CancellationTokenSource();

        var reporter = progress is null
            ? Task.CompletedTask
            : ReportProgressAsync(rpc, partPath, progress, stopwatch, stallLimit, reporting.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, rpc).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _live.TryRemove(process, out _);
            _helpers.Forget(process.Id);

            await reporting.CancelAsync().ConfigureAwait(false);

            try
            {
                await reporter.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the reporter is cancelled by design.
            }
        }

        if (process.ExitCode == 0)
        {
            return;
        }

        string message;

        lock (errors)
        {
            message = errors.Count > 0 ? string.Join("; ", errors.TakeLast(3)) : "no output";
        }

        throw new InvalidOperationException(
            $"aria2c exited with code {process.ExitCode}: {message}");
    }

    /// <summary>
    /// Reports transfer progress, from aria2's own numbers where it can and from
    /// the file on disk where it cannot.
    /// </summary>
    /// <param name="rpc">The RPC client, or <see langword="null"/> when the interface was not opened.</param>
    /// <param name="partPath">The file being written, or <see langword="null"/> for a torrent.</param>
    /// <param name="progress">Receiver for progress updates.</param>
    /// <param name="stopwatch">Elapsed time since the transfer began.</param>
    /// <param name="stallLimit">
    /// How long a stall is tolerated, or <see langword="null"/> when the transfer
    /// has no deadline. Reported rather than enforced here: aria2 owns the
    /// deadline, and this only says how much of it has elapsed.
    /// </param>
    /// <param name="cancellationToken">Stops reporting when the transfer ends.</param>
    /// <remarks>
    /// <para>
    /// aria2 knows things the file cannot show: the total size before the file
    /// has reached it, the rate it is actually achieving across every connection,
    /// and — for a torrent — the peers and seeders it has found. A torrent has no
    /// single growing file to measure at all, so before this it could only report
    /// an indeterminate bar.
    /// </para>
    /// <para>
    /// Falling back matters as much as the RPC does. Until the interface has
    /// answered once, the file is measured anyway, so a bar starts moving
    /// immediately rather than waiting to find out whether a port is going to
    /// come up; and if it never does, that is simply where things stay.
    /// Statistics are worth polling for and never worth a stalled-looking
    /// download, let alone a failed one.
    /// </para>
    /// </remarks>
    private async Task ReportProgressAsync(
        Aria2RpcClient? rpc,
        string? partPath,
        IProgress<DownloadProgress> progress,
        Stopwatch stopwatch,
        TimeSpan? stallLimit,
        CancellationToken cancellationToken)
    {
        var lastBytes = 0L;
        var lastElapsed = TimeSpan.Zero;
        var rate = 0d;

        var answered = false;

        // When the transfer last moved. Kept here rather than derived from the
        // rate alone, because aria2's averaged figure reaches zero a moment
        // after the bytes stop and a countdown that reset on that would flicker.
        var movedAt = TimeSpan.Zero;
        var seenBytes = 0L;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ProgressInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (rpc is not null)
            {
                Aria2Status? status = null;

                try
                {
                    status = await rpc.TellActiveAsync(cancellationToken).ConfigureAwait(false);
                    answered = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException ||
                                           !cancellationToken.IsCancellationRequested)
                {
                    // Expected while aria2c is still binding its port. Given up on
                    // only once the grace period has passed without one good
                    // answer; a single late failure after it has been working is
                    // not a reason to stop asking.
                    if (!answered && stopwatch.Elapsed > RpcGrace)
                    {
                        _logger.LogDebug(
                            ex,
                            "aria2's RPC interface did not answer within {Grace}; " +
                            "reporting progress from the file alone.",
                            RpcGrace);

                        rpc = null;
                    }
                }

                if (status is not null)
                {
                    if (status.CompletedBytes > seenBytes)
                    {
                        seenBytes = status.CompletedBytes;
                        movedAt = stopwatch.Elapsed;
                    }

                    var stalled = stopwatch.Elapsed - movedAt;

                    progress.Report(new DownloadProgress(
                        status.CompletedBytes,
                        status.TotalBytes,
                        status.BytesPerSecond,
                        stopwatch.Elapsed)
                    {
                        Peers = status.Connections,
                        Seeders = status.Seeders,
                        ResolvingMetadata = status.MetadataPending,

                        // Reported with or without a deadline. A torrent has one
                        // and an HTTP transfer does not, but "nothing has arrived
                        // for a while" is worth saying either way — it was the
                        // silence, not the missing countdown, that made a dead
                        // transfer look identical to a slow one.
                        StalledFor = stalled > StallThreshold ? stalled : null,

                        StallLimit = stallLimit
                    });

                    continue;
                }

                // Once it has answered, aria2 is authoritative: nothing active
                // means the transfer has not started or has finished, and
                // measuring a file behind its back would contradict it.
                if (answered)
                {
                    continue;
                }
            }

            if (partPath is null || !File.Exists(partPath))
            {
                continue;
            }

            long bytes;

            try
            {
                bytes = new FileInfo(partPath).Length;
            }
            catch (IOException)
            {
                // The file is being written; a missed sample only costs one
                // progress update.
                continue;
            }

            var elapsed = stopwatch.Elapsed;
            var interval = (elapsed - lastElapsed).TotalSeconds;

            if (interval > 0)
            {
                var instantaneous = (bytes - lastBytes) / interval;

                // Smoothed for the same reason as the HTTP transport: raw rates
                // jitter enough to make a remaining-time estimate unusable.
                // aria2's own figure needs none of this — it is already averaged.
                rate = rate <= 0 ? instantaneous : (rate * 0.7) + (instantaneous * 0.3);
            }

            lastBytes = bytes;
            lastElapsed = elapsed;

            progress.Report(new DownloadProgress(bytes, null, Math.Max(0, rate), elapsed));
        }
    }

    /// <summary>
    /// Creates an RPC session, or reports that statistics will be unavailable.
    /// </summary>
    /// <returns>The session, or <see langword="null"/> when no port could be reserved.</returns>
    private Aria2RpcSession? TryCreateSession()
    {
        try
        {
            return Aria2RpcSession.Create();
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "No loopback port was available for aria2's RPC interface.");
            return null;
        }
    }

    /// <summary>
    /// Stops aria2, politely first.
    /// </summary>
    /// <param name="process">The running process.</param>
    /// <param name="rpc">Its RPC client, or <see langword="null"/>.</param>
    /// <returns>A task that completes once the process has gone.</returns>
    /// <remarks>
    /// <para>
    /// A killed aria2 may not have written its control file, and without that
    /// file the next attempt cannot tell which pieces of the part file are real —
    /// so it starts again from nothing. Asking it to shut down is the difference
    /// between a paused download resuming and a paused download restarting.
    /// </para>
    /// <para>
    /// Killed anyway if it does not go, and the tree with it: aria2 spawns
    /// nothing, but a wrapper script might, and an orphan holding the part file
    /// open would break the next attempt to resume it.
    /// </para>
    /// </remarks>
    private async Task StopAsync(Process process, Aria2RpcClient? rpc)
    {
        if (rpc is not null && !process.HasExited)
        {
            using var grace = new CancellationTokenSource(ShutdownGrace);

            try
            {
                await rpc.ShutdownAsync(grace.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);

                _logger.LogDebug("aria2c shut down cleanly, so its control file is intact.");
                return;
            }
            catch (Exception ex)
            {
                // Unreachable, refused, or simply too slow. The kill below is the
                // answer to all of them.
                _logger.LogDebug(ex, "aria2c did not shut down when asked; ending it.");
            }
        }

        TryKill(process);
    }

    /// <summary>
    /// Finds aria2c through the shared tool locator.
    /// </summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The executable path, or <see langword="null"/> when it is not usable.</returns>
    /// <remarks>
    /// The locator looks beside the executable, in the bundled tools folder, in
    /// the per-user tools folder and finally on PATH, so a copy shipped with the
    /// installer is found without anyone configuring anything — and one the user
    /// installed themselves still is.
    /// </remarks>
    private Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        _tools.LocateAsync("aria2c", _settings.Current.Aria2ExecutablePath, cancellationToken);

    /// <summary>Lists the entries currently in a directory.</summary>
    /// <param name="directory">The directory to list.</param>
    /// <returns>Full paths of everything in it.</returns>
    private static HashSet<string> SnapshotEntries(string directory) =>
        Directory.Exists(directory)
            ? new HashSet<string>(
                Directory.EnumerateFileSystemEntries(directory), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deletes a file, ignoring failure.</summary>
    /// <param name="path">The file to delete.</param>
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
            // Best effort; a leftover control file is untidy, never incorrect.
        }
    }

    /// <summary>
    /// Ends any aria2c still running, so closing the launcher does not leave one
    /// behind.
    /// </summary>
    /// <remarks>
    /// Synchronous and unconditional. This runs while the application is going
    /// down, when there is no time to ask politely and nobody left to wait for an
    /// answer — a transfer interrupted here resumes from its control file, and an
    /// orphaned process holding that file open would prevent exactly that.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var process in _live.Keys)
        {
            TryKill(process);
        }

        _live.Clear();

        // Closing the job kills anything still in it, which is the backstop for a
        // child that ignored the kill above.
        _job.Dispose();
    }

    /// <summary>Kills a process, ignoring failure.</summary>
    /// <param name="process">The process to kill.</param>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // It exited between the check and the kill, which is the outcome
            // that was wanted anyway.
        }
    }
}
