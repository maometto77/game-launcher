using System.Collections.Concurrent;
using System.Diagnostics;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Library;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Launcher;

/// <summary>
/// Default <see cref="IGameLaunchService"/>, backed by <see cref="Process"/>.
/// </summary>
/// <remarks>
/// <para>
/// Session accounting is driven by the process exit event rather than by polling.
/// Elapsed time is measured with <see cref="Stopwatch"/> — a monotonic source —
/// so a daylight-saving change or an NTP correction part way through a long
/// session cannot distort or negate the playtime credited.
/// </para>
/// <para>
/// Some launchers and installers spawn the real game as a child process and exit
/// immediately. When that happens the session is credited only for as long as the
/// process actually lived, which is honest: the launcher has no reliable way to
/// know which unrelated process is the game.
/// </para>
/// </remarks>
public sealed class GameLaunchService : IGameLaunchService, IDisposable
{
    private readonly IGameRepository _games;
    private readonly IPlaySessionRepository _sessions;
    private readonly IExecutableInspector _inspector;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<GameLaunchService> _logger;
    private readonly ConcurrentDictionary<int, RunningGame> _running = new();

    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="games">Game persistence, used to credit playtime.</param>
    /// <param name="sessions">Session persistence.</param>
    /// <param name="inspector">Validates the executable before it is started.</param>
    /// <param name="settings">Supplies this machine's relay device identifier.</param>
    /// <param name="dispatcher">Used to raise events on the UI thread.</param>
    /// <param name="logger">Logger for launch diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GameLaunchService(
        IGameRepository games,
        IPlaySessionRepository sessions,
        IExecutableInspector inspector,
        ISettingsService settings,
        IUiDispatcher dispatcher,
        ILogger<GameLaunchService> logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public event EventHandler<GameSessionEventArgs>? GameStarted;

    /// <inheritdoc />
    public event EventHandler<GameSessionEventArgs>? GameExited;

    /// <inheritdoc />
    public IReadOnlyCollection<int> RunningGameIds => _running.Keys.ToArray();

    /// <inheritdoc />
    /// <remarks>
    /// When more than one game is running, the most recently started one is
    /// reported, since that is the one the user is actually looking at.
    /// </remarks>
    public string? CurrentGameTitle => _running.Values
        .OrderByDescending(entry => entry.StartedAt)
        .FirstOrDefault()?.Game.Title;

    /// <inheritdoc />
    public bool IsRunning(int gameId) => _running.ContainsKey(gameId);

    /// <inheritdoc />
    public int? GetProcessId(int gameId)
    {
        if (!_running.TryGetValue(gameId, out var entry))
        {
            return null;
        }

        try
        {
            // The process can exit between the lookup and this read, which makes
            // the identifier unavailable rather than wrong.
            return entry.Process.HasExited ? null : entry.Process.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> LaunchAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running.ContainsKey(game.Id))
        {
            throw new InvalidOperationException($"{game.Title} is already running.");
        }

        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
        {
            throw new FileNotFoundException(
                $"The executable for {game.Title} could not be found.", game.ExecutablePath);
        }

        // Validated immediately before starting rather than relying on whatever
        // was true at import time. A game can be uninstalled, moved, or replaced
        // by an updater between being added and being played.
        var validation = await _inspector
            .ValidateAsync(game.ExecutablePath, cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsLaunchable)
        {
            throw new InvalidOperationException(
                validation.Problem ?? $"{game.Title} cannot be launched.");
        }

        if (validation is { IsWarningOnly: true, Problem: { } warning })
        {
            // Logged, not blocked: the user explicitly chose to add and launch
            // this executable, and a heuristic should not override that.
            _logger.LogWarning("Launching {Title} despite a validation warning: {Warning}", game.Title, warning);
        }

        // Prefer the recorded install directory; fall back to the executable's own
        // folder. Games routinely load assets by relative path, so starting them
        // with the launcher's working directory would break them.
        var workingDirectory = !string.IsNullOrWhiteSpace(game.InstallDir) && Directory.Exists(game.InstallDir)
            ? game.InstallDir
            : Path.GetDirectoryName(game.ExecutablePath) ?? Environment.CurrentDirectory;

        var startInfo = new ProcessStartInfo
        {
            FileName = game.ExecutablePath,
            WorkingDirectory = workingDirectory,

            // UseShellExecute lets Windows apply the executable's manifest, so a
            // game that requests elevation shows the normal UAC prompt instead of
            // failing to start.
            UseShellExecute = true
        };

        var startedAt = DateTimeOffset.Now;

        // Stamped with this machine's device id so a future multi-device merge can
        // distinguish two concurrent sessions from one reported twice. Null until
        // the launcher has registered with a relay, which is fine: the session is
        // still identified by its own global key.
        var sessionId = await _sessions
            .StartAsync(game.Id, startedAt, _settings.Current.ActiveDeviceId, cancellationToken)
            .ConfigureAwait(false);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"Windows did not return a process for {game.Title}.");
        }
        catch
        {
            // The session row is already written; close it at zero rather than
            // leaving a phantom "in progress" session for a game that never ran.
            await _sessions
                .CompleteAsync(sessionId, DateTimeOffset.Now, 0, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        var entry = new RunningGame(game, sessionId, process, Stopwatch.StartNew(), startedAt);
        _running[game.Id] = entry;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = OnProcessExitedAsync(entry);

        _logger.LogInformation(
            "Launched {Title} (game {GameId}, session {SessionId}, pid {ProcessId}).",
            game.Title, game.Id, sessionId, process.Id);

        RaiseOnUiThread(GameStarted, new GameSessionEventArgs(game, sessionId, 0));

        // A process that exits between Start and the event subscription would
        // otherwise never be accounted for.
        if (process.HasExited)
        {
            await OnProcessExitedAsync(entry).ConfigureAwait(false);
        }

        return sessionId;
    }

    /// <inheritdoc />
    public async Task<int> ReconcileInterruptedSessionsAsync(CancellationToken cancellationToken = default)
    {
        var open = await _sessions.GetInProgressAsync(cancellationToken).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return 0;
        }

        foreach (var session in open)
        {
            // Credited at zero seconds deliberately; see the interface remarks.
            await _sessions
                .CompleteAsync(session.Id, session.StartedAt, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Closed {Count} play session(s) left open by a previous run; no playtime was credited for them.",
            open.Count);

        return open.Count;
    }

    /// <summary>
    /// Credits playtime and closes the session once a game's process exits.
    /// </summary>
    /// <param name="entry">The tracked run that has ended.</param>
    private async Task OnProcessExitedAsync(RunningGame entry)
    {
        // Process.Exited can fire more than once in edge cases; the first removal
        // wins and any later call becomes a no-op.
        if (!_running.TryRemove(entry.Game.Id, out _))
        {
            return;
        }

        try
        {
            entry.Elapsed.Stop();

            var endedAt = DateTimeOffset.Now;
            var seconds = (long)Math.Round(entry.Elapsed.Elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
            seconds = Math.Max(0, seconds);

            await _sessions
                .CompleteAsync(entry.SessionId, endedAt, seconds, CancellationToken.None)
                .ConfigureAwait(false);

            await _games
                .AddPlaytimeAsync(entry.Game.Id, seconds, endedAt, CancellationToken.None)
                .ConfigureAwait(false);

            // Keep the in-memory copy consistent with what was just persisted, so
            // a details page still holding this instance shows the new total.
            entry.Game.PlaytimeSeconds += seconds;
            entry.Game.LastPlayedAt = endedAt;

            _logger.LogInformation(
                "{Title} exited after {Seconds}s (session {SessionId}).",
                entry.Game.Title, seconds, entry.SessionId);

            RaiseOnUiThread(GameExited, new GameSessionEventArgs(entry.Game, entry.SessionId, seconds));
        }
        catch (Exception ex)
        {
            // Raised on a thread pool thread from the process exit callback, so an
            // escaping exception would take the process down.
            _logger.LogError(ex, "Failed to record the play session for {Title}.", entry.Game.Title);
        }
        finally
        {
            entry.Process.Dispose();
        }
    }

    /// <summary>
    /// Raises an event on the UI thread.
    /// </summary>
    /// <param name="handler">The event to raise; ignored when nothing is subscribed.</param>
    /// <param name="args">Event payload.</param>
    /// <remarks>
    /// <see cref="OnProcessExitedAsync"/> runs on a thread pool thread, and
    /// subscribers update bound collections. Marshalling here rather than in each
    /// subscriber means the interface's UI-thread guarantee holds for every
    /// consumer, present and future.
    /// </remarks>
    private void RaiseOnUiThread(EventHandler<GameSessionEventArgs>? handler, GameSessionEventArgs args)
    {
        if (handler is null)
        {
            return;
        }

        _dispatcher.Invoke(() => handler(this, args));
    }

    /// <summary>
    /// Releases the process handles the service is holding.
    /// </summary>
    /// <remarks>
    /// Only the launcher's handles are released. Games that are still running are
    /// deliberately left running: closing the launcher should not kill the user's
    /// game.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var entry in _running.Values)
        {
            entry.Process.Dispose();
        }

        _running.Clear();
    }

    /// <summary>
    /// A game currently running under the launcher.
    /// </summary>
    /// <param name="Game">The game that was launched.</param>
    /// <param name="SessionId">Identifier of the open session row.</param>
    /// <param name="Process">The live process.</param>
    /// <param name="Elapsed">Monotonic timer measuring the session.</param>
    /// <param name="StartedAt">Wall-clock start time, recorded for display.</param>
    private sealed record RunningGame(
        Game Game,
        int SessionId,
        Process Process,
        Stopwatch Elapsed,
        DateTimeOffset StartedAt);
}
