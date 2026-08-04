using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Launcher;

/// <summary>
/// Describes a game starting or stopping.
/// </summary>
/// <param name="Game">The game concerned.</param>
/// <param name="SessionId">Identifier of the play session row.</param>
/// <param name="ElapsedSeconds">
/// Seconds credited to the session. Zero for a start event.
/// </param>
public sealed record GameSessionEventArgs(Game Game, int SessionId, long ElapsedSeconds);

/// <summary>
/// Starts games and accounts for the time they are played.
/// </summary>
public interface IGameLaunchService
{
    /// <summary>Raised on the UI thread after a game's process has started.</summary>
    event EventHandler<GameSessionEventArgs>? GameStarted;

    /// <summary>Raised on the UI thread after a game's process has exited and playtime has been credited.</summary>
    event EventHandler<GameSessionEventArgs>? GameExited;

    /// <summary>Gets the identifiers of games currently running under the launcher.</summary>
    IReadOnlyCollection<int> RunningGameIds { get; }

    /// <summary>Gets the title of the running game reported as presence, or <see langword="null"/> when none is running.</summary>
    string? CurrentGameTitle { get; }

    /// <summary>Determines whether a game is currently running.</summary>
    /// <param name="gameId">Identifier of the game.</param>
    /// <returns><see langword="true"/> when the launcher has a live process for it.</returns>
    bool IsRunning(int gameId);

    /// <summary>
    /// Gets the process identifier of a running game.
    /// </summary>
    /// <param name="gameId">Identifier of the game.</param>
    /// <returns>
    /// The process identifier, or <see langword="null"/> when the game is not
    /// running under this launcher.
    /// </returns>
    /// <remarks>
    /// Needed by memory-backed achievements, which can only read a process the
    /// launcher actually started. Returning the id rather than the
    /// <see cref="System.Diagnostics.Process"/> keeps ownership of the handle here,
    /// where its lifetime is already managed.
    /// </remarks>
    int? GetProcessId(int gameId);

    /// <summary>
    /// Starts a game and begins tracking the session.
    /// </summary>
    /// <param name="game">The game to launch.</param>
    /// <param name="cancellationToken">Cancels the launch.</param>
    /// <returns>The identifier of the new play session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The game's executable no longer exists.</exception>
    /// <exception cref="InvalidOperationException">The game is already running under this launcher.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows refused to start the process.</exception>
    Task<int> LaunchAsync(Game game, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes out sessions left open by a previous run that ended unexpectedly.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of sessions reconciled.</returns>
    /// <remarks>
    /// A session row is opened at launch and closed at exit. If the launcher is
    /// killed or the machine loses power in between, the row stays open forever.
    /// Startup closes those out crediting zero time, because the only honest
    /// answer to "how long was this played" is that we do not know — and
    /// inventing a duration from the wall clock could credit days of playtime for
    /// a machine that was simply left off.
    /// </remarks>
    Task<int> ReconcileInterruptedSessionsAsync(CancellationToken cancellationToken = default);
}
