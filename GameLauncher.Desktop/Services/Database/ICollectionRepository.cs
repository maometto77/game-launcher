using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Persistence for game collections.
/// </summary>
public interface ICollectionRepository
{
    /// <summary>Gets every collection, ordered by sort position then name.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>All collections.</returns>
    Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a single collection.</summary>
    /// <param name="id">Identifier of the collection.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The collection, or <see langword="null"/> if no such row exists.</returns>
    Task<Collection?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Gets the number of games filed under each collection.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>A map of collection identifier to game count. Empty collections are present with a count of zero.</returns>
    Task<IReadOnlyDictionary<int, int>> GetGameCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts a collection and returns its new identifier.</summary>
    /// <param name="collection">The collection to insert. Its identifier is ignored.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The identifier assigned to the new row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A collection with that name already exists.</exception>
    Task<int> AddAsync(Collection collection, CancellationToken cancellationToken = default);

    /// <summary>Updates a collection's name and sort position.</summary>
    /// <param name="collection">The collection to update.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was updated; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Another collection already has that name.</exception>
    Task<bool> UpdateAsync(Collection collection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection. Games filed under it are un-filed, not deleted.
    /// </summary>
    /// <param name="id">Identifier of the collection to delete.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was deleted; otherwise <see langword="false"/>.</returns>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
