using System.Diagnostics;
using System.Globalization;
using System.IO;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Remembers which download helpers this launcher started, across restarts.
/// </summary>
/// <remarks>
/// <para>
/// A launcher killed rather than closed cannot clean up after itself. An
/// <c>aria2c</c> stranded that way keeps downloading at full speed with nothing
/// watching it: invisible in the Downloads table, still writing into the
/// downloads folder, and still holding the file a fresh attempt would want. Two
/// of them on one file corrupt it between them.
/// </para>
/// <para>
/// <see cref="ChildProcessJob"/> stops new orphans being created. This exists to
/// clear the ones already out there — left by a launcher that ran before the job
/// object existed, or by the rare case where assignment failed.
/// </para>
/// <para>
/// A file of process ids, rather than searching for every <c>aria2c</c> on the
/// machine. Matching by name would find helpers the user runs for their own
/// reasons, and matching by command line needs WMI and is still a guess. This
/// only ever names processes this launcher started itself, so a sweep can never
/// kill something that was not ours.
/// </para>
/// <para>
/// Each entry carries the process's start time as well as its id. Windows reuses
/// process ids, so an id alone could point at something entirely unrelated by
/// the time the next launcher reads it; the pair identifies one process for
/// certain.
/// </para>
/// </remarks>
public sealed class DownloadHelperRegistry
{
    private readonly object _gate = new();
    private readonly IAppPaths _paths;
    private readonly string _helperName;
    private readonly ILogger<DownloadHelperRegistry> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="paths">Resolves where the register is kept.</param>
    /// <param name="logger">Records what was recorded and swept.</param>
    /// <param name="helperName">
    /// Process name a recorded entry must still have to be stopped. Defaults to
    /// the real helper; a test substitutes something it can start freely, because
    /// the point of the check is that a sweep never kills the wrong process and
    /// that cannot be demonstrated with a hardcoded name.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DownloadHelperRegistry(
        IAppPaths paths,
        ILogger<DownloadHelperRegistry> logger,
        string helperName = "aria2c")
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperName);

        _paths = paths;
        _logger = logger;
        _helperName = helperName;
    }

    /// <summary>Gets the file holding the register.</summary>
    private string RegisterFile => Path.Combine(_paths.RootDirectory, "download-helpers.txt");

    /// <summary>
    /// Records a helper this launcher has just started.
    /// </summary>
    /// <param name="process">The started helper.</param>
    /// <exception cref="ArgumentNullException"><paramref name="process"/> is <see langword="null"/>.</exception>
    public void Register(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            var entry = $"{process.Id.ToString(CultureInfo.InvariantCulture)}\t" +
                        $"{process.StartTime.Ticks.ToString(CultureInfo.InvariantCulture)}";

            lock (_gate)
            {
                File.AppendAllLines(RegisterFile, [entry]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                        InvalidOperationException)
        {
            // Bookkeeping. A helper that cannot be recorded is still contained by
            // the job object, which is the primary defence.
            _logger.LogDebug(ex, "Could not record download helper {Pid}.", process.Id);
        }
    }

    /// <summary>
    /// Forgets a helper that has exited.
    /// </summary>
    /// <param name="processId">The helper's process id.</param>
    public void Forget(int processId)
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(RegisterFile))
                {
                    return;
                }

                var kept = File.ReadAllLines(RegisterFile)
                    .Where(line => Parse(line) is not { } entry || entry.ProcessId != processId)
                    .ToArray();

                if (kept.Length == 0)
                {
                    File.Delete(RegisterFile);
                }
                else
                {
                    File.WriteAllLines(RegisterFile, kept);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not forget download helper {Pid}.", processId);
        }
    }

    /// <summary>
    /// Stops every recorded helper that is somehow still running, and clears the
    /// register.
    /// </summary>
    /// <returns>How many were stopped.</returns>
    /// <remarks>
    /// Called once at startup. Anything in the register at that point belongs to a
    /// launcher that is no longer running, because a launcher removes its own
    /// entries as its helpers exit.
    /// </remarks>
    public int Sweep()
    {
        var stopped = 0;

        try
        {
            string[] lines;

            lock (_gate)
            {
                if (!File.Exists(RegisterFile))
                {
                    return 0;
                }

                lines = File.ReadAllLines(RegisterFile);
                File.Delete(RegisterFile);
            }

            foreach (var line in lines)
            {
                if (Parse(line) is { } entry && Stop(entry))
                {
                    stopped++;
                }
            }

            if (stopped > 0)
            {
                _logger.LogWarning(
                    "Stopped {Count} download helper(s) left running by a launcher that did not " +
                    "shut down cleanly. Partly downloaded files are kept, and retrying resumes them.",
                    stopped);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not sweep for abandoned download helpers.");
        }

        return stopped;
    }

    /// <summary>Stops one recorded helper, if it is genuinely still that helper.</summary>
    /// <param name="entry">The recorded identity.</param>
    /// <returns><see langword="true"/> when a process was killed.</returns>
    private bool Stop(Entry entry)
    {
        try
        {
            using var process = Process.GetProcessById(entry.ProcessId);

            // Both have to match. The id alone could have been reused by anything
            // since it was written, and killing an unrelated process would be far
            // worse than leaving a stray download running.
            if (process.HasExited ||
                process.StartTime.Ticks != entry.StartedAt ||
                !process.ProcessName.Equals(_helperName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _logger.LogInformation(
                "Stopping abandoned download helper {Pid}, started {Started}.",
                entry.ProcessId,
                process.StartTime);

            process.Kill(entireProcessTree: true);

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                        System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to touch.
            _logger.LogDebug(ex, "Recorded helper {Pid} could not be stopped.", entry.ProcessId);
            return false;
        }
    }

    /// <summary>Reads one register line.</summary>
    /// <param name="line">The line to read.</param>
    /// <returns>The entry, or <see langword="null"/> when the line is unusable.</returns>
    private static Entry? Parse(string line)
    {
        var parts = line.Split('\t');

        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
               long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var started)
            ? new Entry(id, started)
            : null;
    }

    /// <summary>One recorded helper.</summary>
    /// <param name="ProcessId">Its process id.</param>
    /// <param name="StartedAt">When it started, in ticks, to survive id reuse.</param>
    private readonly record struct Entry(int ProcessId, long StartedAt);
}
