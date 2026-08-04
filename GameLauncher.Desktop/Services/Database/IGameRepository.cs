using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Persistence for library games.
/// </summary>
public interface IGameRepository
{
    /// <summary>Gets every game, ordered by title.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>All games in the library.</returns>
    Task<IReadOnlyList<Game>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a single game.</summary>
    /// <param name="id">Identifier of the game.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The game, or <see langword="null"/> if no such row exists.</returns>
    Task<Game?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a game by its executable path, case-insensitively.
    /// </summary>
    /// <param name="executablePath">Absolute path to the executable.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching game, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Used to avoid adding the same executable twice when scanning a folder the
    /// user has already scanned.
    /// </remarks>
    Task<Game?> FindByExecutablePathAsync(string executablePath, CancellationToken cancellationToken = default);

    /// <summary>Gets games in a collection, ordered by title.</summary>
    /// <param name="collectionId">Identifier of the collection.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The games filed under that collection.</returns>
    Task<IReadOnlyList<Game>> GetByCollectionAsync(int collectionId, CancellationToken cancellationToken = default);

    /// <summary>Gets the most recently played games.</summary>
    /// <param name="limit">Maximum number of games to return.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Games that have been played at least once, most recent first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than one.</exception>
    Task<IReadOnlyList<Game>> GetRecentlyPlayedAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Inserts a game and returns its new identifier.</summary>
    /// <param name="game">The game to insert. Its <see cref="Game.Id"/> is ignored.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The identifier assigned to the new row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> is <see langword="null"/>.</exception>
    Task<int> AddAsync(Game game, CancellationToken cancellationToken = default);

    /// <summary>Updates every mutable column of an existing game.</summary>
    /// <param name="game">The game to update, identified by <see cref="Game.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was updated; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> is <see langword="null"/>.</exception>
    Task<bool> UpdateAsync(Game game, CancellationToken cancellationToken = default);

    /// <summary>Deletes a game and everything that cascades from it.</summary>
    /// <param name="id">Identifier of the game to delete.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was deleted; otherwise <see langword="false"/>.</returns>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds elapsed playtime to a game and stamps it as just played.
    /// </summary>
    /// <param name="gameId">Identifier of the game.</param>
    /// <param name="additionalSeconds">Seconds to add. Must not be negative.</param>
    /// <param name="lastPlayedAt">When the session ended.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was updated; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Applied as a relative <c>SET PlaytimeSeconds = PlaytimeSeconds + n</c>
    /// rather than a read-modify-write, so two sessions of the same game ending
    /// at once cannot overwrite each other's total.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="additionalSeconds"/> is negative.</exception>
    Task<bool> AddPlaytimeAsync(
        int gameId,
        long additionalSeconds,
        DateTimeOffset lastPlayedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Moves a set of games into a collection, or clears their collection.</summary>
    /// <param name="gameIds">Identifiers of the games to move.</param>
    /// <param name="collectionId">Target collection, or <see langword="null"/> to un-file them.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The number of games moved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gameIds"/> is <see langword="null"/>.</exception>
    Task<int> AssignCollectionAsync(
        IReadOnlyCollection<int> gameIds,
        int? collectionId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the number of games in the library.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The total game count.</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the summed playtime across the whole library, in seconds.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Total seconds played across every game.</returns>
    Task<long> GetTotalPlaytimeSecondsAsync(CancellationToken cancellationToken = default);
}
