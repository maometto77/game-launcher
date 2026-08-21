using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Download;

namespace GameLauncher.Desktop.Services.Discovery.Install;

/// <summary>
/// One address a file can be fetched from.
/// </summary>
/// <param name="Url">The address.</param>
/// <param name="FileName">The file name to save as, or <see langword="null"/> to derive one.</param>
/// <param name="Checksum">The expected digest, or <see langword="null"/> when none is published.</param>
/// <param name="SourceKey">Which source reported this mirror.</param>
public sealed record ListingMirror(Uri Url, string? FileName, string? Checksum, string SourceKey);

/// <summary>
/// The outcome of preparing a listing for installation.
/// </summary>
/// <param name="Preparation">
/// What was downloaded and found, or <see langword="null"/> when nothing could
/// be fetched.
/// </param>
/// <param name="Listing">The listing that was installed.</param>
/// <param name="MirrorsTried">How many addresses were attempted.</param>
/// <param name="Message">A user-facing explanation, or <see langword="null"/> on success.</param>
public sealed record ListingInstallResult(
    InstallPreparationResult? Preparation,
    CatalogListing Listing,
    int MirrorsTried,
    string? Message)
{
    /// <summary>Gets a value indicating whether the download and unpack succeeded.</summary>
    public bool Succeeded => Preparation is not null;
}

/// <summary>
/// Installs a game the catalogue knows about.
/// </summary>
/// <remarks>
/// <para>
/// A translator, not a second download stack. It chooses an address and hands a
/// <see cref="InstallFromUrlRequest"/> to the existing install path, which
/// already knows how to resume, verify, unpack and detect executables — and
/// which still stops short of adding anything to the library so the user can
/// confirm what was found.
/// </para>
/// <para>
/// Nothing in <see cref="IDownloadService"/> or
/// <see cref="IInstallFromUrlService"/> changes to support this.
/// </para>
/// </remarks>
public interface IListingInstallService
{
    /// <summary>
    /// Lists the addresses a listing's game can be fetched from, best first.
    /// </summary>
    /// <param name="listing">The listing to install.</param>
    /// <param name="preferredSourceKey">
    /// A source to try first, or <see langword="null"/> for the recorded order.
    /// </param>
    /// <returns>Mirrors in the order they should be tried.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="listing"/> is <see langword="null"/>.</exception>
    IReadOnlyList<ListingMirror> GetMirrors(CatalogListing listing, string? preferredSourceKey = null);

    /// <summary>
    /// Downloads, verifies and unpacks a listing, trying each mirror in turn.
    /// </summary>
    /// <param name="listingId">The listing to install.</param>
    /// <param name="preferredSourceKey">
    /// A source whose addresses should be tried first, or <see langword="null"/>
    /// to take them in the order the catalogue recorded.
    /// </param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>What was prepared, ready for the user to confirm.</returns>
    /// <exception cref="ArgumentException"><paramref name="listingId"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">The listing is unknown.</exception>
    /// <remarks>
    /// A preference reorders rather than restricts. Someone picking a source is
    /// saying which to try first, not that the install should fail if that one
    /// is unreachable — the others stay behind it as fallbacks.
    /// </remarks>
    Task<ListingInstallResult> PrepareAsync(
        string listingId,
        string? preferredSourceKey = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a prepared game to the library and links it back to its listing.
    /// </summary>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="executablePath">The executable the user confirmed.</param>
    /// <param name="installDirectory">Where the game was unpacked.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The imported game, or <see langword="null"/> when the import failed.</returns>
    /// <remarks>
    /// Goes through the ordinary import path, so catalog identity is minted from
    /// the executable exactly as it is for a game added any other way. The only
    /// thing this adds is <see cref="Game.ListingId"/>.
    /// </remarks>
    Task<Game?> CompleteAsync(
        CatalogListing listing,
        string executablePath,
        string installDirectory,
        CancellationToken cancellationToken = default);
}
