using System.Diagnostics;
using System.Globalization;
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
/// Driven through the command line rather than the RPC daemon. The RPC interface
/// reports richer progress, but it means owning a long-lived background process,
/// a port, and a secret — a lot of moving parts for a launcher that downloads a
/// file occasionally. Progress here is read from the size of the file on disk,
/// which needs no parsing of console output and cannot be broken by a change to
/// aria2's display format.
/// </para>
/// <para>
/// If <c>aria2c</c> is missing or disabled this transport reports itself
/// unavailable and the download service falls back to
/// <see cref="HttpDownloadTransport"/>, so nothing depends on it being there.
/// </para>
/// </remarks>
public sealed class Aria2DownloadTransport : IDownloadTransport
{
    /// <summary>How often the growing file is measured to report progress.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait for <c>aria2c --version</c> before giving up on it.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly ISettingsService _settings;
    private readonly ILogger<Aria2DownloadTransport> _logger;

    private readonly SemaphoreSlim _probeGate = new(1, 1);

    private string? _resolvedExecutable;
    private bool _probed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="settings">Supplies whether aria2 is enabled and where it lives.</param>
    /// <param name="logger">Logger for transfer diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Aria2DownloadTransport(ISettingsService settings, ILogger<Aria2DownloadTransport> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
                         ?? throw new InvalidOperationException("aria2c is not available.");

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
            "--bt-stop-timeout=300",
            "--follow-torrent=mem",
            request.Url.AbsoluteUri
        };

        await RunAsync(executable, arguments, partPath: null, progress, cancellationToken)
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
    /// <exception cref="InvalidOperationException">aria2c exited with a failure code.</exception>
    private async Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? partPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
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

        if (!process.Start())
        {
            throw new InvalidOperationException("aria2c could not be started.");
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var stopwatch = Stopwatch.StartNew();
        using var reporting = new CancellationTokenSource();

        var reporter = partPath is null || progress is null
            ? Task.CompletedTask
            : ReportProgressAsync(partPath, progress, stopwatch, reporting.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killing the tree matters: aria2 spawns nothing, but a wrapper
            // script might, and an orphan holding the part file open would break
            // the next attempt to resume it.
            TryKill(process);
            throw;
        }
        finally
        {
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
    /// Reports transfer progress by measuring the growing file.
    /// </summary>
    /// <param name="partPath">The file being written.</param>
    /// <param name="progress">Receiver for progress updates.</param>
    /// <param name="stopwatch">Elapsed time since the transfer began.</param>
    /// <param name="cancellationToken">Stops reporting when the transfer ends.</param>
    /// <remarks>
    /// Measuring the file rather than parsing aria2's console output means the
    /// numbers cannot be broken by a change to its display format, and the
    /// transfer's correctness never depends on the parse.
    /// </remarks>
    private static async Task ReportProgressAsync(
        string partPath,
        IProgress<DownloadProgress> progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var lastBytes = 0L;
        var lastElapsed = TimeSpan.Zero;
        var rate = 0d;

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

            if (!File.Exists(partPath))
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
                rate = rate <= 0 ? instantaneous : (rate * 0.7) + (instantaneous * 0.3);
            }

            lastBytes = bytes;
            lastElapsed = elapsed;

            progress.Report(new DownloadProgress(bytes, null, Math.Max(0, rate), elapsed));
        }
    }

    /// <summary>
    /// Finds aria2c, once, and remembers the answer.
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The executable path, or <see langword="null"/> when it is not usable.</returns>
    private async Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken)
    {
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_probed)
            {
                return _resolvedExecutable;
            }

            var configured = _settings.Current.Aria2ExecutablePath;

            var candidate = !string.IsNullOrWhiteSpace(configured)
                ? configured.Trim()
                : OperatingSystem.IsWindows() ? "aria2c.exe" : "aria2c";

            _resolvedExecutable = await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false)
                ? candidate
                : null;

            _probed = true;

            if (_resolvedExecutable is null)
            {
                _logger.LogInformation(
                    "aria2c was not found ({Candidate}); downloads will use the built-in engine.", candidate);
            }

            return _resolvedExecutable;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>Runs <c>--version</c> to confirm a candidate actually works.</summary>
    /// <param name="executable">Path or command to try.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns><see langword="true"/> when it ran successfully.</returns>
    private async Task<bool> ProbeAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // A missing binary throws Win32Exception; anything else here means it
            // is not usable either. Neither is an error worth surfacing — it just
            // means the fallback engine runs.
            _logger.LogDebug(ex, "Probing {Executable} failed.", executable);
            return false;
        }
    }

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
