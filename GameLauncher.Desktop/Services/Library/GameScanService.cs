using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// Default <see cref="IGameScanService"/>.
/// </summary>
/// <remarks>
/// The walk is iterative rather than recursive and enumerates each directory
/// individually. <c>SearchOption.AllDirectories</c> would be shorter, but it
/// abandons the entire enumeration the moment it meets one folder it cannot
/// read — and a games drive reliably contains at least one. Handling each
/// directory separately means an inaccessible folder costs only that folder.
/// </remarks>
public sealed class GameScanService : IGameScanService
{
    /// <summary>
    /// Directory names skipped outright.
    /// </summary>
    /// <remarks>
    /// These hold redistributables, anti-cheat services and uninstall data. They
    /// contain many executables and never the game itself, so descending into
    /// them produces pure noise.
    /// </remarks>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_commonredist", "commonredist", "redist", "redistributable", "redistributables",
        "directx", "vcredist", "dotnet", "dotnetfx", "prerequisites", "prereq",
        "$pluginsdir", "__installer", "uninstall", "installer",
        "easyanticheat", "battleye", "anticheat",
        "node_modules", ".git", ".svn", "windows", "system32", "syswow64",
        "$recycle.bin", "system volume information"
    };

    private readonly IExecutableInspector _inspector;
    private readonly IGameRepository _games;
    private readonly ILogger<GameScanService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="inspector">Reads metadata from discovered executables.</param>
    /// <param name="games">Used to mark candidates that are already in the library.</param>
    /// <param name="logger">Logger for scan diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GameScanService(
        IExecutableInspector inspector,
        IGameRepository games,
        ILogger<GameScanService> logger)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredGame>> ScanAsync(
        string rootDirectory,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A folder must be supplied.", nameof(rootDirectory));
        }

        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"The folder '{rootDirectory}' does not exist.");
        }

        options ??= ScanOptions.Default;

        // One query up front rather than a lookup per candidate.
        var existing = (await _games.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Select(game => game.ExecutablePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var executables = await Task
            .Run(() => CollectExecutables(rootDirectory, options, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        var candidates = new List<DiscoveredGame>(executables.Count);

        foreach (var path in executables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ExecutableInfo info;
            try
            {
                info = await _inspector.InspectAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                // The file vanished or is locked; it simply is not a candidate.
                _logger.LogDebug(ex, "Skipped {Path} during scan.", path);
                continue;
            }

            candidates.Add(Classify(info, existing, options));
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.IsLikelyGame)
            .ThenBy(candidate => candidate.IsAlreadyInLibrary)
            .ThenByDescending(candidate => candidate.Executable.FileSizeBytes)
            .ThenBy(candidate => candidate.SuggestedTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Scan of {Root} found {Total} executables, {Likely} of which look like games.",
            rootDirectory, ordered.Count, ordered.Count(candidate => candidate.IsLikelyGame));

        return ordered;
    }

    /// <summary>
    /// Walks the tree collecting executable paths.
    /// </summary>
    /// <param name="root">Folder to start from.</param>
    /// <param name="options">Limits applied to the walk.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Paths of every <c>.exe</c> found within the configured limits.</returns>
    private List<string> CollectExecutables(
        string root,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        var directoriesScanned = 0;

        while (pending.Count > 0 && found.Count < options.MaximumResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (directory, depth) = pending.Pop();
            directoriesScanned++;

            progress?.Report(new ScanProgress(directoriesScanned, found.Count, directory));

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.exe"))
                {
                    found.Add(file);

                    if (found.Count >= options.MaximumResults)
                    {
                        _logger.LogWarning(
                            "Scan stopped at the {Limit}-result limit; some executables under {Root} were not examined.",
                            options.MaximumResults, root);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Could not list files in {Directory}.", directory);
                continue;
            }

            if (depth >= options.MaximumDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);

                    if (SkippedDirectories.Contains(name) || name.StartsWith('.'))
                    {
                        continue;
                    }

                    // Junctions and symlinks can point back up the tree; following
                    // them turns the walk into an infinite loop.
                    if (IsReparsePoint(child))
                    {
                        _logger.LogDebug("Skipped reparse point {Directory}.", child);
                        continue;
                    }

                    pending.Push((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Could not list subdirectories of {Directory}.", directory);
            }
        }

        return found;
    }

    /// <summary>
    /// Decides how promising a discovered executable is.
    /// </summary>
    /// <param name="info">Metadata read from the executable.</param>
    /// <param name="existingPaths">Executable paths already in the library.</param>
    /// <param name="options">Limits, for the size threshold.</param>
    /// <returns>The classified candidate.</returns>
    private DiscoveredGame Classify(
        ExecutableInfo info,
        IReadOnlySet<string> existingPaths,
        ScanOptions options)
    {
        var alreadyAdded = existingPaths.Contains(info.Path);

        string? note = null;
        var likely = true;

        if (alreadyAdded)
        {
            note = "Already in your library";
            likely = false;
        }
        else if (!info.IsValidExecutable)
        {
            note = "Not a valid Windows executable";
            likely = false;
        }
        else if (_inspector.IsKnownNonGame(info.FileName))
        {
            note = "Looks like an installer or support tool";
            likely = false;
        }
        else if (info.Subsystem == ExecutableSubsystem.WindowsConsole)
        {
            // Console binaries beside a game are servers, editors and build tools.
            note = "Console application";
            likely = false;
        }
        else if (info.FileSizeBytes < options.MinimumExecutableBytes)
        {
            note = "Very small; probably a helper";
            likely = false;
        }

        return new DiscoveredGame
        {
            Executable = info,
            InstallDirectory = ResolveInstallDirectory(info.Path),
            IsLikelyGame = likely,
            IsAlreadyInLibrary = alreadyAdded,
            Note = note
        };
    }

    /// <summary>
    /// Picks the folder that should be treated as the game's installation root.
    /// </summary>
    /// <param name="executablePath">Absolute path to the executable.</param>
    /// <returns>The install directory.</returns>
    /// <remarks>
    /// Engines commonly bury the executable one or two levels down —
    /// <c>Game/Binaries/Win64/Game.exe</c> for Unreal. Treating that leaf as the
    /// install root would make "delete files on uninstall" remove only the
    /// binaries folder and leave the bulk of the game behind, so recognised
    /// container folders are walked back up.
    /// </remarks>
    internal static string ResolveInstallDirectory(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        string[] containerNames = ["win64", "win32", "x64", "x86", "binaries", "bin"];

        var current = new DirectoryInfo(directory);

        // Bounded to two levels: beyond that the guess stops being reliable and
        // risks nominating a folder that holds several unrelated games.
        for (var i = 0; i < 2; i++)
        {
            if (current.Parent is null ||
                !containerNames.Contains(current.Name, StringComparer.OrdinalIgnoreCase))
            {
                break;
            }

            current = current.Parent;
        }

        return current.FullName;
    }

    /// <summary>Determines whether a directory is a junction or symbolic link.</summary>
    /// <param name="directory">Directory to test.</param>
    /// <returns><see langword="true"/> when the directory is a reparse point.</returns>
    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treated as a reparse point so an unreadable entry is skipped.
            return true;
        }
    }
}
