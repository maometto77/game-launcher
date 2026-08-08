using System.Net.Http;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Desktop.Services.Library;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Install;

/// <summary>
/// Default <see cref="IListingInstallService"/>.
/// </summary>
public sealed class ListingInstallService : IListingInstallService
{
    private readonly ICatalogListingRepository _listings;
    private readonly IInstallFromUrlService _install;
    private readonly IGameImportService _import;
    private readonly IGameRepository _games;
    private readonly ILogger<ListingInstallService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="listings">Supplies the listing and its mirrors.</param>
    /// <param name="install">The existing download, verify and unpack path.</param>
    /// <param name="import">Adds the result to the library.</param>
    /// <param name="games">Records the link back to the listing.</param>
    /// <param name="logger">Logger for install diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ListingInstallService(
        ICatalogListingRepository listings,
        IInstallFromUrlService install,
        IGameImportService import,
        IGameRepository games,
        ILogger<ListingInstallService> logger)
    {
        _listings = listings ?? throw new ArgumentNullException(nameof(listings));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<ListingMirror> GetMirrors(CatalogListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return listing.Downloads
            .Where(download => download.Kind is DownloadKind.Game or DownloadKind.Torrent)
            .Where(download => Uri.TryCreate(download.Url, UriKind.Absolute, out var parsed) &&
                               parsed.Scheme is "http" or "https" or "magnet")

            // Torrents last, whatever their recorded rank. They need an external
            // engine that may not be installed, so a direct address is always
            // tried first and the torrent is a bonus rather than a dependency.
            .OrderBy(download => download.Kind == DownloadKind.Torrent ? 1 : 0)
            .ThenBy(download => download.MirrorRank)
            .Select(download => new ListingMirror(
                new Uri(download.Url),
                download.FileName,
                download.BestChecksum,
                download.SourceKey))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<ListingInstallResult> PrepareAsync(
        string listingId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var listing = await _listings.GetAsync(listingId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"No catalogue listing '{listingId}' exists.");

        if (!listing.IsDownloadable)
        {
            // Not a failure to retry. The source has said this item may be looked
            // at but not taken away, and every attempt would return a 403.
            return new ListingInstallResult(
                null, listing, 0, $"'{listing.Title}' is listed but its source does not allow downloading it.");
        }

        var mirrors = GetMirrors(listing);

        if (mirrors.Count == 0)
        {
            return new ListingInstallResult(null, listing, 0, $"'{listing.Title}' has no download address.");
        }

        var tried = 0;
        string? lastError = null;

        foreach (var mirror in mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tried++;

            try
            {
                var request = new InstallFromUrlRequest
                {
                    Url = mirror.Url,
                    ExpectedChecksum = mirror.Checksum,
                    InstallFolderName = listing.SortTitle
                };

                var prepared = await _install
                    .PrepareAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Prepared '{Title}' from mirror {Mirror} of {Total}.", listing.Title, tried, mirrors.Count);

                return new ListingInstallResult(prepared, listing, tried, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                // Worth another address: this one was unreachable or refused.
                // Any partial file already on disk is reused, because mirrors of
                // the same file serve identical bytes and the checksum at the end
                // is what makes relying on that safe.
                lastError = ex.Message;

                _logger.LogWarning(
                    ex, "Mirror {Mirror} of {Total} failed for '{Title}'.", tried, mirrors.Count, listing.Title);
            }
            catch (InvalidOperationException ex)
            {
                // A failed checksum or an unreadable archive. Another mirror may
                // hold a good copy of the same file, so it is still worth trying.
                lastError = ex.Message;

                _logger.LogWarning(
                    ex, "Mirror {Mirror} of {Total} produced an unusable file for '{Title}'.",
                    tried, mirrors.Count, listing.Title);
            }
        }

        return new ListingInstallResult(
            null,
            listing,
            tried,
            $"Could not download '{listing.Title}' from any of its {mirrors.Count} addresses. {lastError}".Trim());
    }

    /// <inheritdoc />
    public async Task<Game?> CompleteAsync(
        CatalogListing listing,
        string executablePath,
        string installDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var request = new GameImportRequest
        {
            ExecutablePath = executablePath,
            Title = listing.Title,
            InstallDirectory = installDirectory,
            SourceUrl = listing.Downloads.FirstOrDefault()?.Url,
            Tags = listing.Genres
        };

        var result = await _import.ImportAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.Game is null)
        {
            _logger.LogWarning("Importing '{Title}' failed: {Message}", listing.Title, result.Message);
            return null;
        }

        // The only write that links the two subsystems. Catalog identity was
        // already minted by the import above, from the executable now on disk,
        // exactly as it is for a game added by any other route.
        result.Game.ListingId = listing.ListingId;

        await _games.UpdateAsync(result.Game, cancellationToken).ConfigureAwait(false);

        return result.Game;
    }
}
