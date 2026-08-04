using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Persistence for individual play sessions.
/// </summary>
public interface IPlaySessionRepository
{
    /// <summary>Opens a session row for a game that has just launched.</summary>
    /// <param name="gameId">Identifier of the game being played.</param>
    /// <param name="startedAt">When the process started.</param>
    /// <param name="deviceId">
    /// Device recording the session, or <see langword="null"/> when this
    /// installation has not registered with a relay.
    /// </param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The identifier of the new session.</returns>
    /// <remarks>
    /// Assigns the session's global <see cref="PlaySession.SessionKey"/>, which is
    /// what makes a later push idempotent.
    /// </remarks>
    Task<int> StartAsync(
        int gameId,
        DateTimeOffset startedAt,
        string? deviceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets completed sessions that have never been pushed to a relay.
    /// </summary>
    /// <param name="limit">Maximum number of sessions to return.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Unsynchronised sessions, oldest first.</returns>
    /// <remarks>
    /// The outbound queue for playtime: an indexed predicate rather than a diff
    /// against the server. Sessions still in progress are excluded, because their
    /// duration is not yet known.
    /// </remarks>
    Task<IReadOnlyList<PlaySession>> GetUnsyncedAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Stamps sessions as pushed.</summary>
    /// <param name="sessionKeys">Global keys of the sessions that were accepted.</param>
    /// <param name="syncedAt">When they were pushed.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The number of rows stamped.</returns>
    Task<int> MarkSyncedAsync(
        IReadOnlyCollection<string> sessionKeys,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears every session's synchronisation watermark, re-queuing them all.
    /// </summary>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The number of sessions re-queued.</returns>
    /// <remarks>
    /// Used when the launcher is pointed at a different relay, which has seen
    /// none of these sessions. Safe to re-push because each carries a globally
    /// unique key, so a merge recognises it rather than double-counting it.
    /// </remarks>
    Task<int> ResetSyncStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes an open session.</summary>
    /// <param name="sessionId">Identifier of the session to close.</param>
    /// <param name="endedAt">When the process exited.</param>
    /// <param name="durationSeconds">Seconds to credit to the game.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was updated; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="durationSeconds"/> is negative.</exception>
    Task<bool> CompleteAsync(
        int sessionId,
        DateTimeOffset endedAt,
        long durationSeconds,
        CancellationToken cancellationToken = default);

    /// <summary>Gets sessions that were never closed.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Sessions whose end time is still null.</returns>
    /// <remarks>
    /// On a clean run this is empty or holds only the session in progress.
    /// Anything else is the residue of a crash or power loss, which startup
    /// reconciles rather than leaving to accumulate.
    /// </remarks>
    Task<IReadOnlyList<PlaySession>> GetInProgressAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the most recent sessions for one game.</summary>
    /// <param name="gameId">Identifier of the game.</param>
    /// <param name="limit">Maximum number of sessions to return.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Sessions for that game, most recent first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than one.</exception>
    Task<IReadOnlyList<PlaySession>> GetRecentForGameAsync(
        int gameId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the number of completed sessions for one game.</summary>
    /// <param name="gameId">Identifier of the game.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>How many times the game has been played to completion of a session.</returns>
    Task<int> CountForGameAsync(int gameId, CancellationToken cancellationToken = default);
}
