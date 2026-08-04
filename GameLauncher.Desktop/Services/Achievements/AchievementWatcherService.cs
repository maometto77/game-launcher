using System.Collections.Concurrent;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Launcher;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// Decides when achievement evaluation runs.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the engine so that "what counts as earned" and "when do we
/// check" stay independent. The engine can be driven from a test with no timers
/// and no processes; this service can change its schedule without touching a
/// single rule.
/// </para>
/// <para>
/// Three sources: the game lifecycle, a poll while a game is running, and file
/// system notifications for save files a rule watches. Each maps to a trigger,
/// and providers opt in to the triggers they can act on.
/// </para>
/// </remarks>
public sealed class AchievementWatcherService : IHostedService, IDisposable
{
    /// <summary>How often memory-backed achievements are re-checked while a game runs.</summary>
    /// <remarks>
    /// Between one and two seconds is frequent enough that an unlock feels
    /// immediate, and infrequent enough that reading a handful of addresses costs
    /// nothing measurable.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long to wait after a save file changes before reading it.
    /// </summary>
    /// <remarks>
    /// A game writing a save produces several change notifications, and the file
    /// is usually incomplete during them. Waiting briefly means one read of a
    /// finished file rather than several of a half-written one.
    /// </remarks>
    private static readonly TimeSpan SaveFileSettleDelay = TimeSpan.FromSeconds(2);

    private readonly IAchievementEngine _engine;
    private readonly IGameLaunchService _launcher;
    private readonly IAchievementRepository _achievements;
    private readonly IGameRepository _games;
    private readonly ILogger<AchievementWatcherService> _logger;

    private readonly ConcurrentDictionary<int, GameWatch> _watches = new();
    private CancellationTokenSource? _lifetime;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="engine">Runs the providers.</param>
    /// <param name="launcher">Raises game start and exit events.</param>
    /// <param name="achievements">Supplies definitions, to know which files to watch.</param>
    /// <param name="games">Reloads a game after exit so playtime is current.</param>
    /// <param name="logger">Logger for scheduling diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AchievementWatcherService(
        IAchievementEngine engine,
        IGameLaunchService launcher,
        IAchievementRepository achievements,
        IGameRepository games,
        ILogger<AchievementWatcherService> logger)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _launcher.GameStarted += OnGameStarted;
        _launcher.GameExited += OnGameExited;

        // Library-wide achievements are checked once at startup, so a milestone
        // crossed by a session the launcher was not running for is still awarded.
        _ = Task.Run(async () =>
        {
            try
            {
                await _engine
                    .EvaluateLibraryAsync(AchievementTrigger.Startup, _lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The startup achievement pass failed.");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _launcher.GameStarted -= OnGameStarted;
        _launcher.GameExited -= OnGameExited;

        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        foreach (var watch in _watches.Values)
        {
            watch.Dispose();
        }

        _watches.Clear();
    }

    /// <summary>Begins polling and file watching for a game that has started.</summary>
    /// <param name="sender">The launch service.</param>
    /// <param name="e">Details of the session.</param>
    private void OnGameStarted(object? sender, GameSessionEventArgs e) =>
        _ = Task.Run(() => BeginWatchingAsync(e.Game), CancellationToken.None);

    /// <summary>Stops watching and runs the post-exit pass.</summary>
    /// <param name="sender">The launch service.</param>
    /// <param name="e">Details of the session.</param>
    private void OnGameExited(object? sender, GameSessionEventArgs e) =>
        _ = Task.Run(() => FinishWatchingAsync(e.Game), CancellationToken.None);

    /// <summary>Sets up polling and save file watching for a running game.</summary>
    /// <param name="game">The game that started.</param>
    private async Task BeginWatchingAsync(Game game)
    {
        var token = _lifetime?.Token ?? CancellationToken.None;

        try
        {
            var processId = _launcher.GetProcessId(game.Id);

            await _engine
                .EvaluateGameAsync(game, AchievementTrigger.GameStarted, processId, token)
                .ConfigureAwait(false);

            var watch = new GameWatch(game.Id);

            if (!_watches.TryAdd(game.Id, watch))
            {
                watch.Dispose();
                return;
            }

            await StartSaveFileWatchersAsync(game, watch, token).ConfigureAwait(false);
            _ = PollAsync(game, watch, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Setting up achievement watching for {Title} failed.", game.Title);
        }
    }

    /// <summary>Tears down watching and evaluates once the game has exited.</summary>
    /// <param name="game">The game that exited.</param>
    private async Task FinishWatchingAsync(Game game)
    {
        if (_watches.TryRemove(game.Id, out var watch))
        {
            watch.Dispose();
        }

        try
        {
            // Reloaded so the pass sees the playtime this session just credited,
            // rather than the total as it was when the game launched.
            var current = await _games.GetByIdAsync(game.Id).ConfigureAwait(false) ?? game;

            await _engine
                .EvaluateGameAsync(current, AchievementTrigger.GameExited, processId: null)
                .ConfigureAwait(false);

            // Library totals move when a session ends, so they are re-checked too.
            await _engine.EvaluateLibraryAsync(AchievementTrigger.GameExited).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The post-exit achievement pass for {Title} failed.", game.Title);
        }
    }

    /// <summary>Re-checks memory-backed achievements while the game runs.</summary>
    /// <param name="game">The running game.</param>
    /// <param name="watch">The watch to stop on.</param>
    /// <param name="cancellationToken">Cancels polling.</param>
    private async Task PollAsync(Game game, GameWatch watch, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (watch.IsStopped || !_launcher.IsRunning(game.Id))
                {
                    return;
                }

                var processId = _launcher.GetProcessId(game.Id);
                if (processId is null)
                {
                    return;
                }

                await _engine
                    .EvaluateGameAsync(game, AchievementTrigger.RunningPoll, processId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown or when the game exits.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polling achievements for {Title} failed.", game.Title);
        }
    }

    /// <summary>Watches the save files any of the game's rules refer to.</summary>
    /// <param name="game">The running game.</param>
    /// <param name="watch">The watch that owns the watchers.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    private async Task StartSaveFileWatchersAsync(Game game, GameWatch watch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(game.CatalogId))
        {
            return;
        }

        var definitions = await _achievements
            .GetDefinitionsForCatalogAsync(game.CatalogId, cancellationToken)
            .ConfigureAwait(false);

        var directories = definitions
            .Where(definition => string.Equals(
                definition.ProviderKey, Providers.SaveFileAchievementProvider.ProviderKey, StringComparison.OrdinalIgnoreCase))
            .Select(definition => SaveFileTriggerConfig.TryParse(definition.TriggerConfigJson))
            .Where(config => config is not null && !string.IsNullOrWhiteSpace(config.SaveFilePath))
            .Select(config => Path.GetDirectoryName(config!.SaveFilePath))
            .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var directory in directories)
        {
            try
            {
                // Watching the directory rather than the file: many games write a
                // new save to a temporary name and rename it into place, which a
                // file-level watcher never sees.
                var watcher = new FileSystemWatcher(directory!)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                watcher.Changed += (_, _) => watch.RequestSaveFileEvaluation(() => EvaluateSaveFilesAsync(game));
                watcher.Created += (_, _) => watch.RequestSaveFileEvaluation(() => EvaluateSaveFilesAsync(game));
                watcher.Renamed += (_, _) => watch.RequestSaveFileEvaluation(() => EvaluateSaveFilesAsync(game));

                watch.Add(watcher);
                _logger.LogDebug("Watching {Directory} for save changes for {Title}.", directory, game.Title);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A path that cannot be watched costs that one rule its live
                // updates; it is still evaluated when the game exits.
                _logger.LogDebug(ex, "Could not watch {Directory} for {Title}.", directory, game.Title);
            }
        }
    }

    /// <summary>Runs a save-file pass after a change has settled.</summary>
    /// <param name="game">The game whose saves changed.</param>
    private async Task EvaluateSaveFilesAsync(Game game)
    {
        var token = _lifetime?.Token ?? CancellationToken.None;

        try
        {
            await Task.Delay(SaveFileSettleDelay, token).ConfigureAwait(false);

            await _engine
                .EvaluateGameAsync(game, AchievementTrigger.SaveFileChanged, processId: null, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The save file achievement pass for {Title} failed.", game.Title);
        }
    }

    /// <summary>Releases every active watch.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var watch in _watches.Values)
        {
            watch.Dispose();
        }

        _watches.Clear();
        _lifetime?.Dispose();
    }

    /// <summary>
    /// The watchers and coalescing state for one running game.
    /// </summary>
    private sealed class GameWatch : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = [];
        private readonly object _gate = new();
        private int _saveEvaluationQueued;

        /// <summary>Initialises a new watch.</summary>
        /// <param name="gameId">The game being watched.</param>
        public GameWatch(int gameId) => GameId = gameId;

        /// <summary>Gets the game this watch belongs to.</summary>
        public int GameId { get; }

        /// <summary>Gets a value indicating whether the watch has been disposed.</summary>
        public bool IsStopped { get; private set; }

        /// <summary>Takes ownership of a watcher.</summary>
        /// <param name="watcher">The watcher to own.</param>
        public void Add(FileSystemWatcher watcher)
        {
            lock (_gate)
            {
                _watchers.Add(watcher);
            }
        }

        /// <summary>
        /// Requests a save-file pass, coalescing bursts into one.
        /// </summary>
        /// <param name="evaluate">The pass to run.</param>
        /// <remarks>
        /// A single save produces a flurry of notifications. The flag means that
        /// flurry causes one evaluation rather than a dozen, and it is cleared
        /// once the pass has actually run so the next save is not swallowed.
        /// </remarks>
        public void RequestSaveFileEvaluation(Func<Task> evaluate)
        {
            if (IsStopped || Interlocked.Exchange(ref _saveEvaluationQueued, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await evaluate().ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _saveEvaluationQueued, 0);
                }
            });
        }

        /// <summary>Stops and releases every watcher.</summary>
        public void Dispose()
        {
            IsStopped = true;

            lock (_gate)
            {
                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }

                _watchers.Clear();
            }
        }
    }
}
