using System.Collections.Concurrent;
using System.Globalization;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Saves;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Achievements.Emulators;

/// <summary>
/// Default <see cref="IAchievementWatcherService"/>: a file watcher per root.
/// </summary>
/// <remarks>
/// <para>
/// Everything happens off the interface thread. <see cref="FileSystemWatcher"/>
/// raises on a thread-pool thread, the read and the database write are
/// asynchronous, and the unlock event is raised from wherever the work finished.
/// Nothing here touches a dispatcher; the view model that listens marshals for
/// itself, which is the same contract every other service in this application
/// offers.
/// </para>
/// <para>
/// Changes are debounced. A game unlocking three achievements at once produces a
/// burst of notifications for a file that is being rewritten in place, and
/// reading it on the first one would read a truncated file. Waiting for the
/// burst to stop means one read of a finished file instead of several of a
/// half-written one.
/// </para>
/// </remarks>
public sealed class AchievementWatcherService : IAchievementWatcherService, IHostedService, IDisposable
{
    /// <summary>File names worth reading.</summary>
    /// <remarks>
    /// A fixed list rather than a wildcard. These directories also hold saves,
    /// configuration and the emulator's own bookkeeping, and re-reading a save
    /// file on every write would be a great deal of work for nothing.
    /// </remarks>
    private static readonly string[] WatchedFiles =
        ["achievements.json", "achievements.ini", "stats.ini", "achievements.bin", "stats.json"];

    /// <summary>
    /// How long a file must be quiet before it is read.
    /// </summary>
    /// <remarks>
    /// Long enough to cover a rewrite-in-place, short enough that a toast still
    /// feels like it belongs to the thing that just happened.
    /// </remarks>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(750);

    private readonly IExternalAchievementRepository _repository;
    private readonly IGameRepository _games;
    private readonly ISettingsService _settings;
    private readonly ILogger<AchievementWatcherService> _logger;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending =
        new(SavePathNormalizer.Comparer);

    /// <summary>
    /// Held for the length of a scan, so two never overlap.
    /// </summary>
    /// <remarks>
    /// The startup scan and a file-change read can arrive together, and two
    /// passes writing the same rows would both compute "newly unlocked" against
    /// the state before either of them wrote — announcing the same achievement
    /// twice.
    /// </remarks>
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private CancellationTokenSource? _lifetime;
    private IReadOnlyList<AchievementSourceRoot>? _roots;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="repository">Where observations are stored.</param>
    /// <param name="games">Used to link an application id to a library entry.</param>
    /// <param name="settings">Supplies any extra roots the user has configured.</param>
    /// <param name="logger">Logger for watcher diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AchievementWatcherService(
        IExternalAchievementRepository repository,
        IGameRepository games,
        ISettingsService settings,
        ILogger<AchievementWatcherService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public event EventHandler<ExternalAchievementUnlockedEventArgs>? AchievementUnlocked;

    /// <inheritdoc />
    public IReadOnlyList<AchievementSourceRoot> WatchedRoots => EnsureRoots();

    /// <summary>
    /// Resolves the roots, once.
    /// </summary>
    /// <returns>The roots to watch.</returns>
    /// <remarks>
    /// Lazy rather than resolved in <see cref="StartAsync"/>, so a caller that
    /// only wants to scan — the interface's refresh, or a test — does not have to
    /// start the hosted lifetime first to get an answer.
    /// </remarks>
    private IReadOnlyList<AchievementSourceRoot> EnsureRoots() =>
        _roots ??= DiscoverRoots(_settings.Current.AchievementWatchRoots);

    /// <summary>
    /// Lists the roots to watch on this machine.
    /// </summary>
    /// <param name="extraRoots">Directories the user has added.</param>
    /// <returns>Every root, whether or not it exists yet.</returns>
    /// <remarks>
    /// Pure, so the list can be asserted without creating folders. The three
    /// built-in locations are conventions rather than anything documented, which
    /// is exactly why they are named in one visible place instead of being
    /// scattered through the watcher.
    /// </remarks>
    public static IReadOnlyList<AchievementSourceRoot> DiscoverRoots(IEnumerable<string>? extraRoots = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var publicDocuments = Path.Combine(
            Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty, "Documents");

        var roots = new List<AchievementSourceRoot>
        {
            new("goldberg", "Goldberg", Path.Combine(appData, "Goldberg SteamEmu Saves")),
            new("rune", "RUNE", Path.Combine(publicDocuments, "Steam", "RUNE")),
            new("codex", "CODEX", Path.Combine(publicDocuments, "Steam", "CODEX"))
        };

        foreach (var extra in extraRoots ?? [])
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                roots.Add(new AchievementSourceRoot("custom", "Custom", SavePathNormalizer.Normalize(extra)));
            }
        }

        return roots;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();

        foreach (var root in EnsureRoots())
        {
            Watch(root);
        }

        // Off the startup path deliberately. Reading every file under three roots
        // is disk work nobody is waiting for, and a launcher that opened slowly
        // because of it would have made a poor trade.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await ScanAllAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down during the first scan is ordinary.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The initial achievement scan failed.");
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime?.Cancel();

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<int> ScanAllAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var unlocked = 0;

            foreach (var root in EnsureRoots())
            {
                if (!Directory.Exists(root.Root))
                {
                    continue;
                }

                foreach (var file in EnumerateFiles(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    unlocked += await ReadAsync(root, file, cancellationToken).ConfigureAwait(false);
                }
            }

            if (unlocked > 0)
            {
                _logger.LogInformation("Scan found {Count} newly unlocked achievement(s).", unlocked);
            }

            return unlocked;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// Lists the achievement files under one root.
    /// </summary>
    /// <param name="root">The root to walk.</param>
    /// <returns>Full paths of files worth reading.</returns>
    /// <remarks>
    /// One level of application folders, and only the named files inside them.
    /// A recursive walk of a directory that also holds saves could be very large
    /// and would find nothing extra.
    /// </remarks>
    private IEnumerable<string> EnumerateFiles(AchievementSourceRoot root)
    {
        string[] appFolders;

        try
        {
            appFolders = Directory.GetDirectories(root.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not list {Root}.", root.Root);
            yield break;
        }

        foreach (var folder in appFolders)
        {
            foreach (var name in WatchedFiles)
            {
                var candidate = Path.Combine(folder, name);

                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>Starts watching one root, if the platform lets it.</summary>
    /// <param name="root">The root to watch.</param>
    private void Watch(AchievementSourceRoot root)
    {
        if (!Directory.Exists(root.Root))
        {
            // Created later when an emulator first writes there. Not an error and
            // not worth polling for: the startup scan catches it next launch.
            _logger.LogDebug("{Source} root {Path} does not exist; not watching it.", root.SourceKey, root.Root);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(root.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,

                // Generous, because a burst of writes across several games can
                // otherwise overflow the buffer and drop every event in it.
                InternalBufferSize = 64 * 1024
            };

            watcher.Changed += (_, e) => OnChanged(root, e.FullPath);
            watcher.Created += (_, e) => OnChanged(root, e.FullPath);
            watcher.Renamed += (_, e) => OnChanged(root, e.FullPath);

            watcher.Error += (_, e) =>
                _logger.LogWarning(e.GetException(), "The watcher for {Path} faulted.", root.Root);

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);

            _logger.LogInformation("Watching {Source} achievements at {Path}.", root.DisplayName, root.Root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not watch {Path}.", root.Root);
        }
    }

    /// <summary>
    /// Queues a changed file to be read once it has settled.
    /// </summary>
    /// <param name="root">The root it belongs to.</param>
    /// <param name="path">The file that changed.</param>
    /// <remarks>
    /// Each path holds its own timer, restarted by every notification. A game
    /// writing the same file five times in a second is read once, after it stops.
    /// </remarks>
    private void OnChanged(AchievementSourceRoot root, string path)
    {
        if (!WatchedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var lifetime = _lifetime;

        if (lifetime is null || lifetime.IsCancellationRequested)
        {
            return;
        }

        var restarted = new CancellationTokenSource();

        if (_pending.TryRemove(path, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _pending[path] = restarted;

        _ = Task.Run(
            async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    restarted.Token, lifetime.Token);

                try
                {
                    await Task.Delay(SettleDelay, linked.Token).ConfigureAwait(false);
                    await ReadAsync(root, path, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a later write, or the launcher is closing.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reading {Path} failed.", path);
                }
                finally
                {
                    if (_pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, restarted)))
                    {
                        restarted.Dispose();
                    }
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Reads one file and records what it says.
    /// </summary>
    /// <param name="root">The root it belongs to.</param>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many achievements were newly unlocked by it.</returns>
    private async Task<int> ReadAsync(
        AchievementSourceRoot root,
        string path,
        CancellationToken cancellationToken)
    {
        var appId = ReadAppId(path);

        if (appId is not > 0)
        {
            _logger.LogDebug("{Path} is not inside an application folder; skipping it.", path);
            return 0;
        }

        string content;

        try
        {
            // Shared read: the emulator may still have the file open, and opening
            // it exclusively would fail for no reason.
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream);
            content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still being written, or locked. The next notification will bring us
            // back, and a missed read costs nothing.
            _logger.LogDebug(ex, "Could not read {Path} yet.", path);
            return 0;
        }

        var snapshot = EmulatorAchievementParser.Parse(content, appId.Value, root.SourceKey, path);

        if (snapshot.Entries.Count == 0)
        {
            return 0;
        }

        var newlyUnlocked = await _repository
            .ApplySnapshotAsync(snapshot.Entries, DateTimeOffset.Now, cancellationToken)
            .ConfigureAwait(false);

        if (newlyUnlocked.Count == 0)
        {
            return 0;
        }

        var game = await FindGameAsync(appId.Value, cancellationToken).ConfigureAwait(false);

        foreach (var achievement in newlyUnlocked)
        {
            // Statistics have no unlocked state worth announcing; they are stored
            // for the progress bars and nothing else.
            if (achievement.Kind != ExternalAchievementKind.Achievement)
            {
                continue;
            }

            AchievementUnlocked?.Invoke(
                this, new ExternalAchievementUnlockedEventArgs(achievement, game, root.DisplayName));
        }

        return newlyUnlocked.Count;
    }

    /// <summary>
    /// Reads the application id out of a file's own folder name.
    /// </summary>
    /// <param name="path">Full path to the achievement file.</param>
    /// <returns>The id, or <see langword="null"/> when the folder is not one.</returns>
    /// <remarks>
    /// Every one of these writers keeps <c>&lt;root&gt;/&lt;appid&gt;/…</c>, so
    /// the folder name is the id. Some nest one deeper for a per-account folder,
    /// which is why the parent is tried as well.
    /// </remarks>
    private static int? ReadAppId(string path)
    {
        var directory = Path.GetDirectoryName(path);

        for (var depth = 0; depth < 3 && directory is { Length: > 0 }; depth++)
        {
            var name = Path.GetFileName(directory);

            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) &&
                appId > 0)
            {
                return appId;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>Finds the library entry an application id belongs to.</summary>
    /// <param name="steamAppId">The application id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The game, or <see langword="null"/> when the library has none.</returns>
    /// <remarks>
    /// A game the library does not have is an ordinary outcome — the file is
    /// still recorded, and the achievements appear if that game is added later.
    /// </remarks>
    private async Task<Game?> FindGameAsync(int steamAppId, CancellationToken cancellationToken)
    {
        try
        {
            var games = await _games.GetAllAsync(cancellationToken).ConfigureAwait(false);

            return games.FirstOrDefault(game => game.SteamAppId == steamAppId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not match app {AppId} to a library entry.", steamAppId);
            return null;
        }
    }

    /// <summary>Stops every watcher and releases them.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();

        foreach (var pending in _pending.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }

        _pending.Clear();
        _scanGate.Dispose();
    }
}
