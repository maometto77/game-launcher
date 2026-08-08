using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Images;

/// <summary>
/// Default <see cref="IListingImageCache"/>.
/// </summary>
public sealed class ListingImageCache : IListingImageCache
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for catalogue artwork.</summary>
    public const string HttpClientName = "discovery-images";

    private const int BufferSize = 64 * 1024;

    /// <summary>Largest image accepted, as a guard against a mistyped address serving something huge.</summary>
    private const long MaxImageBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Formats WPF's imaging stack can decode.
    /// </summary>
    /// <remarks>
    /// WebP is deliberately absent. It downloads successfully and then renders as
    /// nothing at all, which is a far more confusing failure than a missing
    /// image — the same reason the artwork provider filters it out.
    /// </remarks>
    private static readonly string[] RenderableExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICatalogListingRepository _listings;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<ListingImageCache> _logger;

    /// <summary>
    /// Addresses currently being fetched, so a page of tiles binding to the same
    /// image does not start the same download several times over.
    /// </summary>
    private readonly Dictionary<string, Task<string?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured artwork client.</param>
    /// <param name="listings">Records where an image was cached.</param>
    /// <param name="settings">Supplies the cache size limit.</param>
    /// <param name="paths">Resolves the cache folder.</param>
    /// <param name="logger">Logger for cache diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ListingImageCache(
        IHttpClientFactory httpClientFactory,
        ICatalogListingRepository listings,
        ISettingsService settings,
        IAppPaths paths,
        ILogger<ListingImageCache> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _listings = listings ?? throw new ArgumentNullException(nameof(listings));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(
        string listingId,
        string remoteUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) ||
            !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https"))
        {
            return Task.FromResult<string?>(null);
        }

        var path = ResolvePath(address);

        if (File.Exists(path))
        {
            // Touched so the sweep can order by last use rather than by age.
            TryTouch(path);

            return Task.FromResult<string?>(path);
        }

        Task<string?> fetch;

        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(path, out var existing))
            {
                existing = FetchAsync(listingId, address, path, cancellationToken);
                _inFlight[path] = existing;
            }

            fetch = existing;
        }

        return fetch;
    }

    /// <inheritdoc />
    public async Task<long> SweepAsync(CancellationToken cancellationToken = default)
    {
        var limit = Math.Max(16, _settings.Current.DiscoveryImageCacheMegabytes) * 1024L * 1024L;

        if (!Directory.Exists(_paths.ListingImageDirectory))
        {
            return 0;
        }

        var files = new DirectoryInfo(_paths.ListingImageDirectory)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .ToArray();

        var total = files.Sum(file => file.Length);

        if (total <= limit)
        {
            return 0;
        }

        // Artwork for a game the user has installed is pinned: it is shown on the
        // details page of something they own, and re-fetching it would be a
        // visible regression to save space that is not scarce.
        var pinned = await _listings.GetPinnedImagePathsAsync(cancellationToken).ConfigureAwait(false);
        var freed = 0L;

        foreach (var file in files.OrderBy(file => file.LastAccessTimeUtc))
        {
            if (total - freed <= limit)
            {
                break;
            }

            if (pinned.Contains(file.FullName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                freed += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file being read right now is not worth failing a sweep over.
            }
        }

        if (freed > 0)
        {
            _logger.LogInformation("Freed {Megabytes} MB of catalogue artwork.", freed / (1024 * 1024));
        }

        return freed;
    }

    /// <summary>
    /// Downloads one image into the cache.
    /// </summary>
    /// <param name="listingId">The listing the image belongs to.</param>
    /// <param name="address">The image's address.</param>
    /// <param name="path">Where to cache it.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The cached path, or <see langword="null"/> on any failure.</returns>
    private async Task<string?> FetchAsync(
        string listingId,
        Uri address,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_paths.ListingImageDirectory);

            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var response = await client
                .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxImageBytes)
            {
                _logger.LogDebug("Skipping {Address}: larger than the cache limit.", address);
                return null;
            }

            // Written to a temporary file and moved into place, so a failed or
            // cancelled download never leaves a half-written image where the
            // catalogue expects a whole one.
            var temporary = path + ".part";

            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                             temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             BufferSize, useAsync: true))
            {
                await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);

            await _listings
                .SetImagePathAsync(listingId, address.AbsoluteUri, path, cancellationToken)
                .ConfigureAwait(false);

            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Artwork is an enhancement. A catalogue tile without a cover is a
            // normal outcome, and one unreachable image must not take a page down.
            _logger.LogDebug(ex, "Could not cache {Address}.", address);
            return null;
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.Remove(path);
            }
        }
    }

    /// <summary>
    /// Works out where an address is cached.
    /// </summary>
    /// <param name="address">The image's address.</param>
    /// <returns>An absolute path inside the cache folder.</returns>
    /// <remarks>
    /// Content-addressed by the address itself, never by a name the remote server
    /// supplied — the same rule the artwork service follows. It also makes
    /// invalidation automatic: a changed address is simply a different file.
    /// </remarks>
    private string ResolvePath(Uri address)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(address.AbsoluteUri));
        var name = Convert.ToHexString(digest).ToLowerInvariant();

        var extension = Path.GetExtension(address.AbsolutePath).ToLowerInvariant();

        if (!RenderableExtensions.Contains(extension))
        {
            // Several sources serve images from an extensionless endpoint. The
            // decoder sniffs the content anyway, so the name only has to be
            // stable and unique.
            extension = ".img";
        }

        return Path.Combine(_paths.ListingImageDirectory, name + extension);
    }

    /// <summary>Records that a cached file was used, for the sweep's ordering.</summary>
    /// <param name="path">The file that was used.</param>
    private static void TryTouch(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only affects eviction order, never correctness.
        }
    }
}
