using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Finds an external program the launcher can shell out to.
/// </summary>
/// <remarks>
/// <para>
/// Exists because "is aria2c installed" has more than one right answer. It may
/// be bundled beside the executable, dropped into the tools folder by a user, or
/// on the system path because they installed it themselves. All three are
/// legitimate and the launcher should find any of them without being told.
/// </para>
/// <para>
/// It resolves, it does not install. Nothing here downloads a program: a
/// launcher that silently fetched an executable and ran it would be doing the
/// single most abusable thing a desktop application can do, and the fact that
/// the binary is well known does not change what the mechanism is. Bundling at
/// build time and looking in sensible places covers the same ground with none of
/// that.
/// </para>
/// </remarks>
public interface IExternalToolLocator
{
    /// <summary>
    /// Finds a program and confirms it runs.
    /// </summary>
    /// <param name="toolName">Executable name without an extension, such as <c>aria2c</c>.</param>
    /// <param name="configuredPath">An explicit path from settings, tried first.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The path that worked, or <see langword="null"/> when none did.</returns>
    Task<string?> LocateAsync(
        string toolName,
        string? configuredPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the places a program is looked for, in order.
    /// </summary>
    /// <param name="toolName">Executable name without an extension.</param>
    /// <param name="configuredPath">An explicit path from settings.</param>
    /// <returns>Candidate paths, best first.</returns>
    /// <remarks>
    /// Public so the settings page can show a person exactly where the launcher
    /// looked, which is the difference between "aria2c was not found" and a
    /// message they can act on.
    /// </remarks>
    IReadOnlyList<string> GetSearchPaths(string toolName, string? configuredPath = null);
}

/// <summary>
/// Default <see cref="IExternalToolLocator"/>.
/// </summary>
/// <remarks>
/// Answers are cached per tool for the lifetime of the application. Probing runs
/// a process, and doing that on every download to re-learn something that has
/// not changed would be wasteful; a user who installs the tool while the
/// launcher is open restarts it, which is the same thing every other launcher
/// asks for.
/// </remarks>
public sealed class ExternalToolLocator : IExternalToolLocator, IDisposable
{
    /// <summary>Folder beside the executable that bundled tools are published into.</summary>
    public const string BundledToolsFolder = "tools";

    /// <summary>How long to wait for a probe before deciding the answer is no.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IAppPaths _paths;
    private readonly ILogger<ExternalToolLocator> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="paths">Supplies the per-user folder tools may also live in.</param>
    /// <param name="logger">Logger for resolution diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ExternalToolLocator(IAppPaths paths, ILogger<ExternalToolLocator> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSearchPaths(string toolName, string? configuredPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var executable = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
        var candidates = new List<string>();

        // An explicit setting wins. Someone who has named a path has already
        // decided, and searching past it would ignore them.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath.Trim());
        }

        // Beside the executable, and in the tools folder published next to it.
        // This is where a bundled copy lands.
        var appDirectory = AppContext.BaseDirectory;

        candidates.Add(Path.Combine(appDirectory, executable));
        candidates.Add(Path.Combine(appDirectory, BundledToolsFolder, executable));

        // The per-user folder, so a tool can be added without write access to
        // Program Files.
        candidates.Add(Path.Combine(_paths.RootDirectory, BundledToolsFolder, executable));

        // Last, the bare name: the process launcher resolves it against PATH.
        candidates.Add(executable);

        return candidates;
    }

    /// <inheritdoc />
    public async Task<string?> LocateAsync(
        string toolName,
        string? configuredPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var key = $"{toolName}{configuredPath ?? string.Empty}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_resolved.TryGetValue(key, out var cached))
            {
                return cached;
            }

            foreach (var candidate in GetSearchPaths(toolName, configuredPath))
            {
                // A bare name has no file to check — PATH resolution happens when
                // the process starts — so it goes straight to the probe.
                var isBareName = !candidate.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                                 !candidate.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

                if (!isBareName && !File.Exists(candidate))
                {
                    continue;
                }

                if (!await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                _logger.LogInformation("Found {Tool} at {Path}.", toolName, candidate);

                _resolved[key] = candidate;
                return candidate;
            }

            _logger.LogInformation(
                "{Tool} was not found. Looked in: {Paths}",
                toolName,
                string.Join("; ", GetSearchPaths(toolName, configuredPath)));

            _resolved[key] = null;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs a candidate with <c>--version</c> to confirm it actually works.
    /// </summary>
    /// <param name="executable">Path or bare name to try.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns><see langword="true"/> when it ran and reported success.</returns>
    /// <remarks>
    /// A file existing is not the same as a file running. A zero-byte
    /// placeholder, a copy for the wrong architecture, or something quarantined
    /// by security software all pass <c>File.Exists</c> and fail to start, and
    /// finding that out during a download would be far worse than finding it out
    /// here.
    /// </remarks>
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
            // is not usable either. Neither is worth surfacing — it only means a
            // fallback runs.
            _logger.LogDebug(ex, "Probing {Executable} failed.", executable);
            return false;
        }
    }

    /// <summary>Kills a probe that outstayed its welcome.</summary>
    /// <param name="process">The process to end.</param>
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
            // It exited between the check and the kill, which is the outcome that
            // was wanted anyway.
        }
    }

    /// <summary>Releases the probe gate.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
