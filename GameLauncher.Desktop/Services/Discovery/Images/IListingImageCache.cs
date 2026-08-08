namespace GameLauncher.Desktop.Services.Discovery.Images;

/// <summary>
/// Fetches and caches catalogue artwork on demand.
/// </summary>
/// <remarks>
/// <para>
/// Images are deliberately not downloaded during an import. A catalogue of
/// several thousand games with half a dozen images each is tens of thousands of
/// transfers, which would turn a minutes-long import into an hours-long one and
/// fill the disk with screenshots of games nobody opened.
/// </para>
/// <para>
/// So an import stores addresses, and bytes are fetched the first time something
/// displays them.
/// </para>
/// </remarks>
public interface IListingImageCache
{
    /// <summary>
    /// Gets a local path for an image, fetching it if it is not cached.
    /// </summary>
    /// <param name="listingId">The listing the image belongs to.</param>
    /// <param name="remoteUrl">The image's address.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>
    /// The cached path, or <see langword="null"/> when the image could not be
    /// fetched.
    /// </returns>
    /// <remarks>
    /// Never throws for an unreachable or unreadable image. Artwork is an
    /// enhancement; a catalogue tile without it is a normal outcome, and a
    /// failed fetch must not take a page down with it.
    /// </remarks>
    Task<string?> GetAsync(string listingId, string remoteUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes least-recently-used images until the cache is within its limit.
    /// </summary>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>How many bytes were freed.</returns>
    /// <remarks>
    /// Artwork for a listing the user has actually installed is never evicted.
    /// </remarks>
    Task<long> SweepAsync(CancellationToken cancellationToken = default);
}
