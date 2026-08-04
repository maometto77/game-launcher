using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Local cache of the friend list, so the Friends page has content before the
/// relay connection is established.
/// </summary>
public interface IFriendCacheRepository
{
    /// <summary>Gets every cached friend, ordered by display name.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The cached friend list.</returns>
    Task<IReadOnlyList<FriendCache>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates one cached friend.</summary>
    /// <param name="friend">The friend snapshot to store.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="friend"/> is <see langword="null"/>.</exception>
    Task UpsertAsync(FriendCache friend, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the entire cache with the supplied set, in one transaction.
    /// </summary>
    /// <param name="friends">The authoritative friend list from the relay.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the cache matches <paramref name="friends"/>.</returns>
    /// <remarks>
    /// Used after a successful sync. Replacing wholesale rather than merging is
    /// what removes friends who were deleted while this client was offline; a
    /// merge would leave them in the list forever.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="friends"/> is <see langword="null"/>.</exception>
    Task ReplaceAllAsync(IReadOnlyCollection<FriendCache> friends, CancellationToken cancellationToken = default);

    /// <summary>Removes one friend from the cache.</summary>
    /// <param name="friendCode">Friend code to remove.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was removed; otherwise <see langword="false"/>.</returns>
    Task<bool> DeleteAsync(string friendCode, CancellationToken cancellationToken = default);
}
