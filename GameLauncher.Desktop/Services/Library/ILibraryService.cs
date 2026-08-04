using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// The outcome of an uninstall.
/// </summary>
/// <param name="EntryRemoved">Whether the library row was deleted.</param>
/// <param name="FilesDeleted">Whether the install folder was deleted from disk.</param>
/// <param name="FileDeletionError">
/// A user-facing reason the files could not be deleted, or <see langword="null"/>
/// when no deletion was attempted or it succeeded.
/// </param>
public sealed record UninstallResult(bool EntryRemoved, bool FilesDeleted, string? FileDeletionError);

/// <summary>
/// Application logic for maintaining the game library.
/// </summary>
/// <remarks>
/// Sits between the view models and the repositories. Anything that is more than
/// a single persistence call — deleting files alongside a row, measuring an
/// install, resolving artwork — belongs here rather than in a view model.
/// </remarks>
public interface ILibraryService
{
    /// <summary>Saves a user's free-text notes against a game.</summary>
    /// <param name="gameId">Identifier of the game.</param>
    /// <param name="notes">The notes to store, or <see langword="null"/> to clear them.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when the game was found and updated.</returns>
    Task<bool> SaveNotesAsync(int gameId, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Saves edited metadata for a game.</summary>
    /// <param name="game">The game carrying the edited values.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when the game was found and updated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> is <see langword="null"/>.</exception>
    Task<bool> UpdateAsync(Game game, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a game from the library, optionally deleting its files.
    /// </summary>
    /// <param name="gameId">Identifier of the game to remove.</param>
    /// <param name="deleteFiles">Whether to also delete the install folder.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was actually removed.</returns>
    /// <remarks>
    /// The library row is removed even when file deletion fails, and the failure
    /// is reported rather than thrown. A locked file should not leave the user
    /// with an entry they have already asked to remove and now cannot get rid of.
    /// </remarks>
    Task<UninstallResult> UninstallAsync(
        int gameId,
        bool deleteFiles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Measures the total size of a directory tree.
    /// </summary>
    /// <param name="directory">Directory to measure.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>Total size in bytes, or zero when the directory is absent or unreadable.</returns>
    Task<long> MeasureDirectorySizeAsync(string? directory, CancellationToken cancellationToken = default);
}
