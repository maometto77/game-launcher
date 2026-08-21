using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sources;

/// <summary>
/// Populates the catalogue from the Internet Archive's software libraries.
/// </summary>
/// <remarks>
/// <para>
/// Uses the Archive's own APIs throughout. Its account and collection pages are
/// rendered in the browser, so fetching them as HTML returns nothing but site
/// chrome — scraping was never an option here, and the API is better anyway.
/// </para>
/// <para>
/// Curated software-library items carry a <c>mobygames_*</c> block with
/// structured genre, developer and publisher, which is why this source outranks
/// prose-parsing ones for those fields. Every file also carries <c>sha1</c> and
/// <c>md5</c>, so integrity verification comes free through the existing
/// download path.
/// </para>
/// </remarks>
public sealed class InternetArchiveCatalogSource : ICatalogSource
{
    /// <summary>Dispatch key stored on every row this source contributes.</summary>
    public const string SourceKey = "internet-archive";

    /// <summary>Name of the configured <see cref="HttpClient"/> used for Archive requests.</summary>
    public const string HttpClientName = "discovery-internet-archive";

    private const string ScrapeEndpoint = "https://archive.org/services/search/v1/scrape";
    private const string MetadataEndpoint = "https://archive.org/metadata";
    private const string DownloadEndpoint = "https://archive.org/download";
    private const string ThumbnailEndpoint = "https://archive.org/services/img";

    /// <summary>
    /// Page size for enumeration.
    /// </summary>
    /// <remarks>
    /// The scrape API rejects anything below 100 outright, so this is a floor
    /// rather than a preference.
    /// </remarks>
    private const int PageSize = 100;

    /// <summary>Extensions treated as a game download.</summary>
    private static readonly string[] DownloadableExtensions =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".iso", ".exe", ".img", ".dsk", ".d64", ".adf"];

    /// <summary>Extensions treated as a screenshot.</summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif"];

    /// <summary>Collections that identify the platform an item is for.</summary>
    private static readonly (string Collection, string Platform)[] PlatformCollections =
    [
        ("softwarelibrary_msdos", "DOS"),
        ("softwarelibrary_win3", "Windows 3.x"),
        ("softwarelibrary_apple", "Apple II"),
        ("softwarelibrary_c64", "Commodore 64"),
        ("softwarelibrary_zx_spectrum", "ZX Spectrum"),
        ("softwarelibrary_atari", "Atari"),
        ("softwarelibrary_amiga", "Amiga"),
        ("open_source_software", "Windows")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<InternetArchiveCatalogSource> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured Archive client.</param>
    /// <param name="settings">Supplies which collections to import.</param>
    /// <param name="logger">Logger for source diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public InternetArchiveCatalogSource(
        IHttpClientFactory httpClientFactory,
        ISettingsService settings,
        ILogger<InternetArchiveCatalogSource> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => SourceKey;

    /// <inheritdoc />
    public string DisplayName => "Internet Archive";

    /// <summary>
    /// Ranked first.
    /// </summary>
    /// <remarks>
    /// Its structured fields come from a curated database rather than from prose,
    /// and it is the only source that supplies a per-file checksum. Per-field
    /// rules still override this — a rank only breaks ties.
    /// </remarks>
    public int Rank => 0;

    /// <summary>
    /// Four concurrent requests, no enforced spacing.
    /// </summary>
    /// <remarks>
    /// A large content-delivery network with a documented, public API meant for
    /// programmatic access. This is a very different neighbour from a small site,
    /// and treating them the same would either be rude to one or needlessly slow
    /// against the other.
    /// </remarks>
    public SourceThrottle Throttle => new(4, TimeSpan.Zero);

    /// <summary>
    /// Available only once the user has switched discovery on and chosen at
    /// least one collection.
    /// </summary>
    /// <remarks>
    /// Opt-in on purpose, and the switch defaults to off. Discovery reaches out
    /// to a third-party service and pulls down several thousand records; a
    /// launcher that began doing that on first run, without being asked, would
    /// be taking a decision that belongs to the person running it.
    /// </remarks>
    public bool IsAvailable =>
        _settings.Current.DiscoveryEnabled && (Collections.Count > 0 || Uploader is not null);

    /// <summary>Gets the collections configured for import.</summary>
    private IReadOnlyList<string> Collections => _settings.Current.InternetArchiveCollections;

    /// <summary>Gets the uploader configured for import, or <see langword="null"/>.</summary>
    private string? Uploader =>
        _settings.Current.InternetArchiveUploader is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceListingRef> EnumerateAsync(
        SourceEnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var query = BuildQuery(options.ChangedSince, options.Query);

        if (query is null)
        {
            yield break;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        // The cursor that produced the page being yielded, not the one after it.
        // Resuming therefore replays the page a kill interrupted, which the
        // pipeline's content-hash check makes almost free — whereas resuming one
        // page too late would silently skip up to a hundred items.
        var pageCursor = options.Cursor;
        var yielded = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await ScrapeAsync(client, query, pageCursor, cancellationToken).ConfigureAwait(false);

            if (page is null || page.Items.Count == 0)
            {
                yield break;
            }

            foreach (var item in page.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Identifier))
                {
                    continue;
                }

                yield return new SourceListingRef(
                    SourceKey,
                    item.Identifier,
                    item.Title ?? item.Identifier,
                    ParseUnixSeconds(item.LastUpdated),
                    pageCursor);

                if (options.MaxItems > 0 && ++yielded >= options.MaxItems)
                {
                    yield break;
                }
            }

            if (string.IsNullOrEmpty(page.Cursor))
            {
                yield break;
            }

            pageCursor = page.Cursor;
        }
    }

    /// <inheritdoc />
    public async Task<SourceListing?> FetchAsync(
        SourceListingRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = $"{MetadataEndpoint}/{Uri.EscapeDataString(reference.SourceItemId)}";

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        // An item that has been removed is a normal answer, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var metadata = InternetArchiveMetadata.Parse(payload);

        if (metadata is null || !metadata.IsPresent)
        {
            return null;
        }

        // A deleted item's metadata endpoint answers with an empty object rather
        // than a 404, which is why presence is checked as well as the status.
        if (metadata.MediaType is { } mediaType &&
            !string.Equals(mediaType, "software", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Skipping {Identifier}: mediatype is {MediaType}, not software.",
                reference.SourceItemId, mediaType);

            return null;
        }

        return Map(metadata, payload);
    }

    /// <summary>
    /// Turns an Archive item into a source observation.
    /// </summary>
    /// <param name="metadata">The parsed item.</param>
    /// <param name="payload">The response body, stored for later re-parsing.</param>
    /// <returns>The observation.</returns>
    private static SourceListing Map(InternetArchiveMetadata metadata, string payload)
    {
        var identifier = metadata.Identifier;
        var restricted = metadata.IsDownloadRestricted;

        var downloads = restricted ? [] : BuildDownloads(metadata, identifier);

        return new SourceListing
        {
            SourceKey = SourceKey,
            SourceItemId = identifier,
            SourceUrl = new Uri($"https://archive.org/details/{Uri.EscapeDataString(identifier)}"),
            Title = metadata.GetString("title") ?? identifier,
            Year = metadata.GetYear(),
            Description = metadata.GetString("description"),

            // The curated block first, then the generic Dublin Core fields the
            // Archive applies to everything.
            Developer = metadata.GetString("mobygames_developed_by") ?? metadata.GetString("creator"),
            Publisher = metadata.GetString("mobygames_published_by") ?? metadata.GetString("publisher"),

            // The curated field where it exists — most items have none, so the
            // subject list is mined as a fallback, but strictly: an unrecognised
            // subject is a tag, not a genre, and letting them through would fill
            // the genre facet with "emulation" and "dosbox".
            Genres = MapGenres(metadata),
            Platforms = BuildPlatforms(metadata),
            Tags = metadata.GetStrings("subject").Take(12).ToArray(),
            Images = BuildImages(metadata, identifier),
            Downloads = downloads,
            IsDownloadable = !restricted && downloads.Count > 0,
            SourceUpdatedAt = metadata.LastUpdated,
            RawPayload = payload
        };
    }

    /// <summary>
    /// Works out an item's genres.
    /// </summary>
    /// <param name="metadata">The parsed item.</param>
    /// <returns>Canonical genres, or an empty list when none can be told.</returns>
    /// <remarks>
    /// Curated items carry <c>mobygames_genre</c>, which is a controlled
    /// vocabulary and is taken as given. The great majority do not, so their
    /// subject list is mined instead — but only for values already recognised as
    /// genres, because a subject list is a general-purpose tag field and most of
    /// what is in it is not a genre at all.
    /// </remarks>
    private static IReadOnlyList<string> MapGenres(InternetArchiveMetadata metadata)
    {
        var curated = GenreVocabulary.MapMany(metadata.GetStrings("mobygames_genre"));

        return curated.Count > 0 ? curated : GenreVocabulary.MapKnown(metadata.GetStrings("subject"));
    }

    /// <summary>
    /// Builds the download list, including direct mirrors.
    /// </summary>
    /// <param name="metadata">The parsed item.</param>
    /// <param name="identifier">The item identifier.</param>
    /// <returns>Downloads, canonical address first.</returns>
    /// <remarks>
    /// Rank 0 is always the redirector, which re-resolves to a working server on
    /// every request and therefore never goes stale. The two direct hosts follow
    /// as real alternates: they are faster and they survive the redirector
    /// itself being unreachable, but an item that the Archive later moves leaves
    /// them pointing at nothing — which is exactly why they are not first.
    /// </remarks>
    private static IReadOnlyList<ListingDownloadRef> BuildDownloads(
        InternetArchiveMetadata metadata,
        string identifier)
    {
        var downloads = new List<ListingDownloadRef>();

        foreach (var file in metadata.Files)
        {
            if (!file.IsOriginal || !DownloadableExtensions.Contains(file.Extension))
            {
                continue;
            }

            var encoded = Uri.EscapeDataString(file.Name);
            var rank = downloads.Count;

            downloads.Add(new ListingDownloadRef
            {
                Url = new Uri($"{DownloadEndpoint}/{Uri.EscapeDataString(identifier)}/{encoded}"),
                FileName = file.Name,
                SizeBytes = file.Size,
                Md5 = file.Md5,
                Sha1 = file.Sha1,
                Format = file.Format,
                Kind = DownloadKind.Game,
                MirrorRank = rank
            });

            foreach (var host in new[] { metadata.PrimaryHost, metadata.SecondaryHost })
            {
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(metadata.Directory))
                {
                    continue;
                }

                downloads.Add(new ListingDownloadRef
                {
                    Url = new Uri($"https://{host}{metadata.Directory}/{encoded}"),
                    FileName = file.Name,
                    SizeBytes = file.Size,
                    Md5 = file.Md5,
                    Sha1 = file.Sha1,
                    Format = file.Format,
                    Kind = DownloadKind.Game,
                    MirrorRank = downloads.Count
                });
            }
        }

        AppendTorrent(metadata, identifier, downloads);

        return downloads;
    }

    /// <summary>
    /// Adds the item's own <c>.torrent</c>, when the Archive publishes one.
    /// </summary>
    /// <param name="metadata">The parsed item.</param>
    /// <param name="identifier">The item identifier.</param>
    /// <param name="downloads">The list being built.</param>
    /// <remarks>
    /// <para>
    /// The Archive generates a torrent for most items and asks that large
    /// transfers use it, because peers carry the load instead of its own
    /// servers. For a multi-gigabyte preservation archive that is both faster
    /// for the person downloading and kinder to the host.
    /// </para>
    /// <para>
    /// Ranked last on purpose. It only works when aria2c is installed and
    /// enabled, so it must never be the mirror an install reaches for first —
    /// the HTTP addresses above always work.
    /// </para>
    /// </remarks>
    private static void AppendTorrent(
        InternetArchiveMetadata metadata,
        string identifier,
        List<ListingDownloadRef> downloads)
    {
        // Some items opt out, and the flag is the Archive saying so.
        if (downloads.Count == 0 ||
            string.Equals(metadata.GetString("noarchivetorrent"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var torrent = metadata.Files.FirstOrDefault(file =>
            file.Name.EndsWith("_archive.torrent", StringComparison.OrdinalIgnoreCase));

        if (torrent is null)
        {
            return;
        }

        downloads.Add(new ListingDownloadRef
        {
            Url = new Uri(
                $"{DownloadEndpoint}/{Uri.EscapeDataString(identifier)}/{Uri.EscapeDataString(torrent.Name)}"),
            FileName = torrent.Name,

            // The size of the .torrent file itself, not of what it delivers, so
            // it is deliberately not reported as the download size.
            SizeBytes = null,
            Md5 = null,
            Sha1 = null,
            Format = "Torrent",
            Kind = DownloadKind.Torrent,
            MirrorRank = downloads.Count
        });
    }

    /// <summary>
    /// Builds the image list.
    /// </summary>
    /// <param name="metadata">The parsed item.</param>
    /// <param name="identifier">The item identifier.</param>
    /// <returns>A cover followed by any screenshots.</returns>
    /// <remarks>
    /// The cover is the Archive's own thumbnail service rather than a file from
    /// the item. It exists for every item, it is already sized for a tile, and
    /// using it means a catalogue of several thousand games can show artwork
    /// without downloading several thousand full-size images.
    /// </remarks>
    private static IReadOnlyList<ListingImageRef> BuildImages(
        InternetArchiveMetadata metadata,
        string identifier)
    {
        var images = new List<ListingImageRef>
        {
            new(
                new Uri($"{ThumbnailEndpoint}/{Uri.EscapeDataString(identifier)}"),
                ListingImageKind.Cover,
                0,
                0,
                0)
        };

        foreach (var file in metadata.Files)
        {
            // Derivatives are the Archive's own thumbnails of these same files.
            if (!file.IsOriginal ||
                !ImageExtensions.Contains(file.Extension) ||
                file.Name.StartsWith("__ia", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            images.Add(new ListingImageRef(
                new Uri(
                    $"{DownloadEndpoint}/{Uri.EscapeDataString(identifier)}/{Uri.EscapeDataString(file.Name)}"),
                ListingImageKind.Screenshot,
                0,
                0,
                images.Count));
        }

        return images;
    }

    /// <summary>
    /// Works out which platforms an item is for.
    /// </summary>
    /// <param name="metadata">The parsed item.</param>
    /// <returns>Platform names, or an empty list when none can be told.</returns>
    /// <remarks>
    /// Derived from the collections the item belongs to, which is the only field
    /// the Archive fills in consistently. <c>mobygames_also_for</c> is
    /// deliberately ignored: it lists platforms the <em>title</em> was released
    /// on elsewhere, not what this copy runs on, and treating it as the latter
    /// would offer a DOS download under a Macintosh filter.
    /// </remarks>
    private static IReadOnlyList<string> BuildPlatforms(InternetArchiveMetadata metadata)
    {
        var platforms = new List<string>();

        foreach (var collection in metadata.Collections)
        {
            foreach (var (prefix, platform) in PlatformCollections)
            {
                if (collection.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !platforms.Contains(platform, StringComparer.OrdinalIgnoreCase))
                {
                    platforms.Add(platform);
                }
            }
        }

        if (platforms.Count == 0 &&
            string.Equals(metadata.GetString("emulator"), "dosbox", StringComparison.OrdinalIgnoreCase))
        {
            platforms.Add("DOS");
        }

        return platforms;
    }

    /// <summary>
    /// Builds the fielded search query.
    /// </summary>
    /// <param name="changedSince">Only items changed since this point, or <see langword="null"/>.</param>
    /// <param name="search">Free text to match against the title, or <see langword="null"/>.</param>
    /// <returns>The query, or <see langword="null"/> when nothing is configured.</returns>
    /// <remarks>
    /// The scrape API rejects a bare free-text query outright, so every term here
    /// is fielded. The media type is pinned as well as the collection because a
    /// software collection still contains the odd text or image item.
    /// </remarks>
    private string? BuildQuery(DateTimeOffset? changedSince, string? search = null)
    {
        var terms = Collections
            .Where(collection => !string.IsNullOrWhiteSpace(collection))
            .Select(collection => $"collection:\"{collection.Trim()}\"")
            .ToList();

        // Combined with the collections rather than replacing them, so one pass
        // can cover curated libraries and a particular person's uploads.
        if (Uploader is { } uploader)
        {
            terms.Add($"uploader:\"{uploader}\"");
        }

        if (terms.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        builder.Append('(')
            .AppendJoin(" OR ", terms)
            .Append(')')
            .Append(" AND mediatype:software");

        if (changedSince is { } since)
        {
            // The index stores dates by day, so the window is widened by one to
            // avoid missing an item changed later on the boundary day.
            var from = since.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            builder.Append(CultureInfo.InvariantCulture, $" AND addeddate:[{from} TO 9999-12-31]");
        }

        if (Escape(search) is { Length: > 0 } term)
        {
            // Narrowed to the configured collections rather than searching the
            // whole Archive. The settings say which corner of it this catalogue
            // is for, and a search that ignored them would import items from
            // collections the user deliberately did not ask for.
            builder.Append(CultureInfo.InvariantCulture, $" AND title:({term})");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reduces free text to something safe to place inside a fielded query.
    /// </summary>
    /// <param name="text">What the person typed.</param>
    /// <returns>The safe remainder, possibly empty.</returns>
    /// <remarks>
    /// <para>
    /// An allow-list, not an escape. The query is assembled as text and the
    /// index speaks a Lucene-like syntax, so a quotation mark or bracket in the
    /// search box does not merely fail to match — it closes the term this is
    /// substituted into and starts another. <c>") OR collection:("</c> typed
    /// into a search box would otherwise widen a query that the settings
    /// deliberately narrowed.
    /// </para>
    /// <para>
    /// Letters, digits, spaces and the few punctuation marks that occur in real
    /// titles survive; everything else is dropped rather than escaped, because
    /// the escaping rules differ between the endpoints this project talks to and
    /// a search term loses nothing worth keeping by being conservative.
    /// </para>
    /// </remarks>
    private static string Escape(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var safe = new StringBuilder(text.Length);

        foreach (var character in text.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '\'' or '.' or '&')
            {
                safe.Append(character);
                continue;
            }

            // Anything else becomes a gap rather than vanishing, so two words do
            // not run together into a term matching neither. Runs are collapsed,
            // because a stripped "):(" would otherwise leave three spaces where
            // the reader expects one.
            if (safe.Length > 0 && safe[^1] != ' ')
            {
                safe.Append(' ');
            }
        }

        return safe.ToString().Trim();
    }

    /// <summary>
    /// Fetches one page of search results.
    /// </summary>
    /// <param name="client">The configured Archive client.</param>
    /// <param name="query">The fielded query.</param>
    /// <param name="cursor">Continuation token, or <see langword="null"/> for the first page.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page, or <see langword="null"/> when it could not be read.</returns>
    private async Task<ScrapePage?> ScrapeAsync(
        HttpClient client,
        string query,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var url = new StringBuilder(ScrapeEndpoint)
            .Append("?q=").Append(Uri.EscapeDataString(query))
            .Append("&fields=identifier,title,year,item_last_updated")
            .Append("&count=").Append(PageSize.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(cursor))
        {
            url.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        }

        using var response = await client.GetAsync(url.ToString(), cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await JsonSerializer
                .DeserializeAsync<ScrapePage>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The Internet Archive returned a search page that could not be read.");
            return null;
        }
    }

    /// <summary>Parses a Unix timestamp the search index returns as a number.</summary>
    /// <param name="seconds">Seconds since the epoch, or <see langword="null"/>.</param>
    /// <returns>The instant, or <see langword="null"/>.</returns>
    private static DateTimeOffset? ParseUnixSeconds(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    /// <summary>One page of scrape results.</summary>
    private sealed class ScrapePage
    {
        /// <summary>The items on this page.</summary>
        public List<ScrapeItem> Items { get; set; } = [];

        /// <summary>Continuation token for the next page, or <see langword="null"/> at the end.</summary>
        public string? Cursor { get; set; }

        /// <summary>How many items match the query in total.</summary>
        public long Total { get; set; }
    }

    /// <summary>One item in a page of scrape results.</summary>
    private sealed class ScrapeItem
    {
        /// <summary>The Archive's identifier for the item.</summary>
        public string? Identifier { get; set; }

        /// <summary>The item's title.</summary>
        /// <remarks>
        /// Read through <see cref="FlexibleStringConverter"/> because the search
        /// index returns this as an array whenever an item carries more than one
        /// title. Typed as a plain string it throws, and because the page is
        /// deserialised in one pass a single such item discards every result
        /// alongside it.
        /// </remarks>
        [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleStringConverter))]
        public string? Title { get; set; }

        /// <summary>When the Archive last changed the item, in Unix seconds.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("item_last_updated")]
        public long? LastUpdated { get; set; }
    }
}
