using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Sources;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing;

/// <summary>
/// Sourcing adapter for the Internet Archive.
/// </summary>
/// <remarks>
/// <para>
/// Answers the question the catalogue import cannot: given an
/// <c>archive.org</c> address, what can be fetched right now. The import knows
/// only what it recorded when it last ran, which leaves three cases uncovered —
/// a listing whose download rows were never written because another source
/// described it first, an item whose files changed after the import, and an
/// address a person pasted in that was never imported at all.
/// </para>
/// <para>
/// It reads the public metadata API, the same documented endpoint the catalogue
/// source uses, and reports every original file with the checksums the Archive
/// publishes alongside the item's <c>.torrent</c>. No page is scraped: the
/// Archive renders its item pages in the browser, so there is nothing in the
/// HTML to read even if scraping were the right approach.
/// </para>
/// <para>
/// This adapter deliberately handles <em>any</em> Archive item rather than the
/// collections the catalogue happens to be configured for. The two settings
/// answer different questions — which collections to import wholesale, and
/// whether this particular address can supply a file — and conflating them would
/// mean a game found through one collection could not be installed from a link
/// belonging to another.
/// </para>
/// </remarks>
public sealed class InternetArchiveSourcingAdapter : ISourcingAdapter
{
    private const string SiteHost = "archive.org";
    private const string MetadataEndpoint = "https://archive.org/metadata";
    private const string DownloadEndpoint = "https://archive.org/download";

    /// <summary>Paths that carry an item identifier as their next segment.</summary>
    private static readonly string[] ItemPathPrefixes =
        ["/details/", "/download/", "/metadata/", "/stream/", "/compress/", "/serve/", "/embed/"];

    /// <summary>Extensions treated as a game download.</summary>
    /// <remarks>
    /// The same list the catalogue source uses. Two lists that had to agree and
    /// were maintained separately would eventually not.
    /// </remarks>
    private static readonly string[] DownloadableExtensions =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".iso", ".exe", ".img", ".dsk", ".d64", ".adf"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InternetArchiveSourcingAdapter> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured Archive client.</param>
    /// <param name="logger">Logger for sourcing diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public InternetArchiveSourcingAdapter(
        IHttpClientFactory httpClientFactory,
        ILogger<InternetArchiveSourcingAdapter> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => InternetArchiveCatalogSource.SourceKey;

    /// <inheritdoc />
    public string DisplayName => "Internet Archive";

    /// <inheritdoc />
    public bool CanHandle(string url) => TryReadIdentifier(url, out _);

    /// <inheritdoc />
    public async Task<SourcingPayload> ExtractDownloadPayloadAsync(
        CatalogListing listing,
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!TryReadIdentifier(url, out var identifier))
        {
            return SourcingPayload.Unsupported;
        }

        var client = _httpClientFactory.CreateClient(InternetArchiveCatalogSource.HttpClientName);

        string json;

        try
        {
            using var response = await client
                .GetAsync($"{MetadataEndpoint}/{Uri.EscapeDataString(identifier)}", cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new SourcingPayload(
                    [],
                    SourcingRefusal.NoPayload,
                    $"The Archive has no item called '{identifier}'.");
            }

            response.EnsureSuccessStatusCode();

            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogWarning(ex, "The Archive could not be reached for '{Identifier}'.", identifier);

            return new SourcingPayload(
                [],
                SourcingRefusal.Unreachable,
                $"The Internet Archive could not be reached: {ex.Message}");
        }

        var metadata = InternetArchiveMetadata.Parse(json);

        if (metadata is not { IsPresent: true })
        {
            return new SourcingPayload(
                [],
                SourcingRefusal.NoPayload,
                $"The Archive returned nothing usable for '{identifier}'.");
        }

        // An empty metadata response is also how the Archive answers for an item
        // that has been darkened, so this is checked before the files are read.
        if (metadata.IsDownloadRestricted)
        {
            _logger.LogDebug("Archive item '{Identifier}' is access-restricted.", identifier);

            return new SourcingPayload(
                [],
                SourcingRefusal.NoPayload,
                $"The Archive allows '{listing.Title}' to be viewed but not downloaded.");
        }

        var downloads = BuildDownloads(metadata, identifier, listing.ListingId);

        return downloads.Count > 0
            ? new SourcingPayload(downloads)
            : new SourcingPayload(
                [],
                SourcingRefusal.NoPayload,
                $"The Archive item '{identifier}' holds no file this launcher can install.");
    }

    /// <summary>
    /// Reads the item identifier out of an Archive address.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="identifier">The identifier, when there is one.</param>
    /// <returns><see langword="true"/> when the address names an item.</returns>
    /// <remarks>
    /// The Archive uses one identifier across a family of paths —
    /// <c>/details/</c> for a person, <c>/download/</c> for a file, and several
    /// others — so all of them are accepted rather than only the one the
    /// catalogue happens to record. A bare host with no item is not a match:
    /// there is nothing to fetch.
    /// </remarks>
    private static bool TryReadIdentifier(string? url, out string identifier)
    {
        identifier = string.Empty;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (!parsed.Host.Equals(SiteHost, StringComparison.OrdinalIgnoreCase) &&
            !parsed.Host.EndsWith("." + SiteHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = parsed.AbsolutePath;

        var prefix = ItemPathPrefixes.FirstOrDefault(candidate =>
            path.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));

        if (prefix is null)
        {
            return false;
        }

        // The segment after the prefix, and only that: '/download/doom/doom.zip'
        // names the item 'doom', not the file.
        var remainder = path[prefix.Length..];
        var end = remainder.IndexOf('/', StringComparison.Ordinal);

        var candidate = Uri.UnescapeDataString(end < 0 ? remainder : remainder[..end]).Trim();

        if (candidate.Length == 0)
        {
            return false;
        }

        identifier = candidate;
        return true;
    }

    /// <summary>
    /// Builds the download rows for an item.
    /// </summary>
    /// <param name="metadata">The parsed item.</param>
    /// <param name="identifier">The item identifier.</param>
    /// <param name="listingId">Listing the rows belong to.</param>
    /// <returns>Direct addresses, their mirrors, then the torrent.</returns>
    /// <remarks>
    /// <para>
    /// The redirector first, then the two node hosts the metadata names. The
    /// direct hosts are faster and survive the redirector being unreachable, but
    /// an item the Archive later moves leaves them pointing at nothing — which is
    /// why they are alternates rather than the first choice.
    /// </para>
    /// <para>
    /// The <c>.torrent</c> comes last of all. It needs aria2c, which may not be
    /// installed, so it must never be the address an install reaches for first.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ListingDownload> BuildDownloads(
        InternetArchiveMetadata metadata,
        string identifier,
        string listingId)
    {
        var downloads = new List<ListingDownload>();
        var item = Uri.EscapeDataString(identifier);

        foreach (var file in metadata.Files)
        {
            if (!file.IsOriginal || !DownloadableExtensions.Contains(file.Extension))
            {
                continue;
            }

            var encoded = Uri.EscapeDataString(file.Name);

            downloads.Add(Row(
                listingId, $"{DownloadEndpoint}/{item}/{encoded}", file, DownloadKind.Game, downloads.Count));

            foreach (var host in new[] { metadata.PrimaryHost, metadata.SecondaryHost })
            {
                if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(metadata.Directory))
                {
                    downloads.Add(Row(
                        listingId,
                        $"https://{host}{metadata.Directory}/{encoded}",
                        file,
                        DownloadKind.Game,
                        downloads.Count));
                }
            }
        }

        if (downloads.Count == 0 ||
            string.Equals(metadata.GetString("noarchivetorrent"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return downloads;
        }

        var torrent = metadata.Files.FirstOrDefault(file =>
            file.Name.EndsWith("_archive.torrent", StringComparison.OrdinalIgnoreCase));

        if (torrent is not null)
        {
            downloads.Add(new ListingDownload
            {
                ListingId = listingId,
                SourceKey = InternetArchiveCatalogSource.SourceKey,
                Url = $"{DownloadEndpoint}/{item}/{Uri.EscapeDataString(torrent.Name)}",
                FileName = torrent.Name,

                // The size and digests of the .torrent file itself describe the
                // pointer, not what it delivers, so reporting them would have the
                // download service verify the wrong thing.
                SizeBytes = null,
                Format = "Torrent",
                Kind = DownloadKind.Torrent,
                MirrorRank = downloads.Count
            });
        }

        return downloads;
    }

    /// <summary>Builds one download row from an Archive file.</summary>
    /// <param name="listingId">Listing the row belongs to.</param>
    /// <param name="url">Address to fetch it from.</param>
    /// <param name="file">The file the Archive described.</param>
    /// <param name="kind">What the address delivers.</param>
    /// <param name="rank">Preference order among mirrors.</param>
    /// <returns>The row.</returns>
    private static ListingDownload Row(
        string listingId,
        string url,
        InternetArchiveFile file,
        DownloadKind kind,
        int rank) =>
        new()
        {
            ListingId = listingId,
            SourceKey = InternetArchiveCatalogSource.SourceKey,
            Url = url,
            FileName = file.Name,
            SizeBytes = file.Size,
            Md5 = file.Md5,
            Sha1 = file.Sha1,
            Format = file.Format,
            Kind = kind,
            MirrorRank = rank
        };
}
