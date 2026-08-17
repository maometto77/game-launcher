using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sources;

/// <summary>
/// Populates the catalogue with metadata from MyAbandonware.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Metadata only, and deliberately so.</strong> The site's
/// <c>robots.txt</c> disallows <c>/download/*</c> for every crawler, so this
/// source never collects, stores or follows a download address. It contributes
/// titles, years, developers, publishers, genres, platforms and screenshots; a
/// game it alone describes is listed but not installable, and one it shares with
/// a source that does publish downloads becomes installable through that source.
/// </para>
/// <para>
/// Extraction reads the page's <c>schema.org/VideoGame</c> JSON-LD first, which
/// carries every field this source contributes in a structured form and changes
/// far less often than the markup around it. Selectors exist only as a fallback.
/// </para>
/// <para>
/// Enumeration uses the sitemap the site advertises in its own
/// <c>robots.txt</c>: one compressed file rather than a walk over hundreds of
/// browse pages, which is both faster and the gentler thing to do.
/// </para>
/// </remarks>
public sealed class MyAbandonwareCatalogSource : ICatalogSource
{
    /// <summary>Dispatch key stored on every row this source contributes.</summary>
    public const string SourceKey = "myabandonware";

    /// <summary>Name of the configured <see cref="HttpClient"/> used for this site.</summary>
    public const string HttpClientName = "discovery-myabandonware";

    private const string SiteRoot = "https://www.myabandonware.com";
    private const string SitemapUrl = SiteRoot + "/sitemap.xml.gz";

    /// <summary>Largest number of child sitemaps read in one pass.</summary>
    /// <remarks>
    /// A bound rather than a preference. It stops a malformed or hostile index
    /// from turning one enumeration into an unbounded crawl.
    /// </remarks>
    private const int MaxChildSitemaps = 40;

    private static readonly HtmlParser Parser = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRobotsPolicy _robots;
    private readonly ISettingsService _settings;
    private readonly ILogger<MyAbandonwareCatalogSource> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured client.</param>
    /// <param name="robots">Decides which paths may be fetched.</param>
    /// <param name="settings">Supplies whether discovery is switched on.</param>
    /// <param name="logger">Logger for source diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MyAbandonwareCatalogSource(
        IHttpClientFactory httpClientFactory,
        IRobotsPolicy robots,
        ISettingsService settings,
        ILogger<MyAbandonwareCatalogSource> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _robots = robots ?? throw new ArgumentNullException(nameof(robots));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => SourceKey;

    /// <inheritdoc />
    public string DisplayName => "MyAbandonware";

    /// <summary>
    /// Ranked after the Internet Archive.
    /// </summary>
    /// <remarks>
    /// Its titles are cleaner and it is the only source here with platform and
    /// genre for most titles, but the Archive's curated fields come from a
    /// maintained database. Per-field rules override this in both directions —
    /// a rank only breaks ties.
    /// </remarks>
    public int Rank => 1;

    /// <summary>
    /// One request at a time, well spaced.
    /// </summary>
    /// <remarks>
    /// A small site, not a content-delivery network. One request at a time in a
    /// tight loop is still a request every few milliseconds, which is exactly
    /// the traffic pattern that earns a block — so the spacing matters more than
    /// the concurrency limit.
    /// </remarks>
    public SourceThrottle Throttle => new(1, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Available only when discovery is on and the source is enabled.
    /// </summary>
    public bool IsAvailable =>
        _settings.Current.DiscoveryEnabled && _settings.Current.MyAbandonwareEnabled;

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceListingRef> EnumerateAsync(
        SourceEnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var yielded = 0;

        var sitemaps = await ResolveSitemapsAsync(client, cancellationToken).ConfigureAwait(false);
        var resuming = !string.IsNullOrEmpty(options.Cursor);

        foreach (var sitemap in sitemaps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The cursor is the sitemap being read, so a resumed pass replays one
            // file rather than starting the whole site again.
            if (resuming)
            {
                if (!string.Equals(sitemap, options.Cursor, StringComparison.Ordinal))
                {
                    continue;
                }

                resuming = false;
            }

            var entries = await ReadSitemapAsync(client, sitemap, cancellationToken).ConfigureAwait(false);

            foreach (var entry in entries)
            {
                if (options.ChangedSince is { } since && entry.LastModified is { } modified &&
                    modified <= since)
                {
                    continue;
                }

                yield return new SourceListingRef(
                    SourceKey, entry.Slug, entry.Slug, entry.LastModified, sitemap);

                if (options.MaxItems > 0 && ++yielded >= options.MaxItems)
                {
                    yield break;
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<SourceListing?> FetchAsync(
        SourceListingRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var address = new Uri($"{SiteRoot}/game/{reference.SourceItemId}");

        // Checked before every request, not once at startup. A site can change
        // its mind, and the answer is cached per host so this is nearly free.
        if (!await _robots.IsAllowedAsync(address, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("robots.txt disallows {Address}; skipping it.", address);
            return null;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client.GetAsync(address, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return Map(html, reference.SourceItemId, address);
    }

    /// <summary>
    /// Turns a game page into a source observation.
    /// </summary>
    /// <param name="html">The page as served.</param>
    /// <param name="slug">The item's identifier within the site.</param>
    /// <param name="address">The page's address.</param>
    /// <returns>The observation, or <see langword="null"/> when it is not a game page.</returns>
    /// <remarks>
    /// Internal so the mapping can be tested against a captured page without a
    /// server: parsing is where this source's real work happens.
    /// </remarks>
    internal static SourceListing? Map(string html, string slug, Uri address)
    {
        var document = Parser.ParseDocument(html);
        var game = FindVideoGame(document);

        var title = ReadString(game, "name") ?? Meta(document, "og:title") ?? Text(document, "h1");

        if (string.IsNullOrWhiteSpace(title))
        {
            // A page that yields no title is a parse failure, not an empty
            // record. Returning something blank would let a site redesign look
            // like a successful import of nothing.
            return null;
        }

        return new SourceListing
        {
            SourceKey = SourceKey,
            SourceItemId = slug,
            SourceUrl = address,
            Title = title.Trim(),
            Year = ReadYear(game),
            Developer = CompanyNormalizer.Clean(ReadFirst(game, "author")),
            Publisher = CompanyNormalizer.Clean(ReadFirst(game, "publisher")),
            Genres = GenreVocabulary.MapMany(ReadAll(game, "genre")),
            Platforms = ReadAll(game, "gamePlatform"),

            // og:description here is template text — "Remember X, an old video
            // game from 1992? Download it and play again on MyAbandonware." —
            // which is worse than no description at all, because the merge would
            // rank it against a real one by length.
            Description = null,

            Images = ReadImages(document, address),

            // Never populated. robots.txt disallows /download/*, so this source
            // does not collect download addresses at all.
            Downloads = [],
            IsDownloadable = false,

            RawPayload = html
        };
    }

    /// <summary>
    /// Finds the page's schema.org VideoGame block.
    /// </summary>
    /// <param name="document">The parsed page.</param>
    /// <returns>The block, or <see langword="null"/> when the page has none.</returns>
    /// <remarks>
    /// Preferred over every selector on the page. It is published for search
    /// engines, which means the site has a strong reason to keep it stable and
    /// correct — far more so than the class names around it.
    /// </remarks>
    private static JsonElement? FindVideoGame(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            JsonDocument parsed;

            try
            {
                parsed = JsonDocument.Parse(script.TextContent);
            }
            catch (JsonException)
            {
                continue;
            }

            using (parsed)
            {
                if (TryFindVideoGame(parsed.RootElement, out var found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>Walks a JSON-LD document looking for a VideoGame node.</summary>
    /// <param name="element">The element to search.</param>
    /// <param name="game">The node when found.</param>
    /// <returns><see langword="true"/> when a VideoGame node is present.</returns>
    private static bool TryFindVideoGame(JsonElement element, out JsonElement game)
    {
        game = default;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("@type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), "VideoGame", StringComparison.OrdinalIgnoreCase))
                {
                    game = element.Clone();
                    return true;
                }

                // A @graph wrapper is a common shape and hides the node one level
                // down, so nesting is walked rather than assumed away.
                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindVideoGame(property.Value, out game))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindVideoGame(item, out game))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>Reads a string property from the game node.</summary>
    /// <param name="game">The node, if any.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static string? ReadString(JsonElement? game, string name) =>
        game?.TryGetProperty(name, out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads the first entry of a property that may be a string or an array.</summary>
    /// <param name="game">The node, if any.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The first value, or <see langword="null"/>.</returns>
    private static string? ReadFirst(JsonElement? game, string name) => ReadAll(game, name).FirstOrDefault();

    /// <summary>Reads every entry of a property that may be a string or an array.</summary>
    /// <param name="game">The node, if any.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The values, or an empty list.</returns>
    private static IReadOnlyList<string> ReadAll(JsonElement? game, string name)
    {
        if (game?.TryGetProperty(name, out var value) != true)
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Wrap(value.GetString()),
            JsonValueKind.Array => value.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString()!)
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .ToArray(),
            _ => []
        };

        static IReadOnlyList<string> Wrap(string? single) =>
            string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
    }

    /// <summary>Reads the release year.</summary>
    /// <param name="game">The node, if any.</param>
    /// <returns>The year, or <see langword="null"/>.</returns>
    private static int? ReadYear(JsonElement? game)
    {
        var published = ReadString(game, "datePublished");

        if (string.IsNullOrWhiteSpace(published))
        {
            return null;
        }

        if (int.TryParse(published, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            return year;
        }

        return published.Length >= 4 &&
               int.TryParse(published[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix)
            ? prefix
            : null;
    }

    /// <summary>
    /// Collects the page's screenshots.
    /// </summary>
    /// <param name="document">The parsed page.</param>
    /// <param name="address">The page's address, for resolving relative links.</param>
    /// <returns>Images, cover first.</returns>
    /// <remarks>
    /// Thumbnail variants are skipped in favour of the full-size image beside
    /// them; the launcher decodes to the size it needs, so caching a thumbnail
    /// would just mean a blurry tile.
    /// </remarks>
    private static IReadOnlyList<ListingImageRef> ReadImages(IDocument document, Uri address)
    {
        var images = new List<ListingImageRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Meta(document, "og:image") is { Length: > 0 } cover &&
            Uri.TryCreate(address, cover, out var coverUrl) &&
            seen.Add(coverUrl.AbsoluteUri))
        {
            images.Add(new ListingImageRef(coverUrl, ListingImageKind.Cover, 0, 0, 0));
        }

        foreach (var element in document.QuerySelectorAll("img[src*='/media/screenshots/']"))
        {
            var source = element.GetAttribute("src");

            if (string.IsNullOrWhiteSpace(source) ||
                source.Contains("/thumbs/", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(address, source, out var url) ||
                !seen.Add(url.AbsoluteUri))
            {
                continue;
            }

            images.Add(new ListingImageRef(url, ListingImageKind.Screenshot, 0, 0, images.Count));
        }

        return images;
    }

    /// <summary>Reads an OpenGraph meta value.</summary>
    /// <param name="document">The parsed page.</param>
    /// <param name="property">The property name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static string? Meta(IDocument document, string property) =>
        document.QuerySelector($"meta[property='{property}']")?.GetAttribute("content");

    /// <summary>Reads an element's text.</summary>
    /// <param name="document">The parsed page.</param>
    /// <param name="selector">The selector to read.</param>
    /// <returns>The trimmed text, or <see langword="null"/>.</returns>
    private static string? Text(IDocument document, string selector) =>
        document.QuerySelector(selector)?.TextContent?.Trim();

    /// <summary>
    /// Works out which sitemaps hold game pages.
    /// </summary>
    /// <param name="client">The configured client.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>Sitemap addresses, in order.</returns>
    private async Task<IReadOnlyList<string>> ResolveSitemapsAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var document = await ReadXmlAsync(client, SitemapUrl, cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            return [];
        }

        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        // A sitemap index points at further sitemaps; a plain urlset is the
        // whole thing. Both shapes are legal and the site may switch between them.
        var children = document.Root?
            .Elements(ns + "sitemap")
            .Select(entry => entry.Element(ns + "loc")?.Value)
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .Select(location => location!)
            .Take(MaxChildSitemaps)
            .ToArray() ?? [];

        return children.Length > 0 ? children : [SitemapUrl];
    }

    /// <summary>
    /// Reads one sitemap's game entries.
    /// </summary>
    /// <param name="client">The configured client.</param>
    /// <param name="url">The sitemap to read.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>Game entries found in it.</returns>
    private async Task<IReadOnlyList<SitemapEntry>> ReadSitemapAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        var document = await ReadXmlAsync(client, url, cancellationToken).ConfigureAwait(false);

        if (document?.Root is null)
        {
            return [];
        }

        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var entries = new List<SitemapEntry>();

        foreach (var element in document.Root.Elements(ns + "url"))
        {
            var location = element.Element(ns + "loc")?.Value;

            if (string.IsNullOrWhiteSpace(location) ||
                !Uri.TryCreate(location, UriKind.Absolute, out var address))
            {
                continue;
            }

            var slug = ExtractSlug(address);

            if (slug is null)
            {
                continue;
            }

            DateTimeOffset? modified = null;

            if (DateTimeOffset.TryParse(
                    element.Element(ns + "lastmod")?.Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                modified = parsed;
            }

            entries.Add(new SitemapEntry(slug, modified));
        }

        return entries;
    }

    /// <summary>
    /// Extracts a game slug from a page address.
    /// </summary>
    /// <param name="address">The address from the sitemap.</param>
    /// <returns>The slug, or <see langword="null"/> when it is not a game page.</returns>
    /// <remarks>
    /// Only <c>/game/{slug}</c> qualifies. Deeper paths such as
    /// <c>/game/{slug}/play-1id</c> are a different page about the same game and
    /// would otherwise be imported as a second listing.
    /// </remarks>
    internal static string? ExtractSlug(Uri address)
    {
        var segments = address.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 2 &&
               segments[0].Equals("game", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(segments[1])
            : null;
    }

    /// <summary>
    /// Fetches an XML document, decompressing it when it is gzipped.
    /// </summary>
    /// <param name="client">The configured client.</param>
    /// <param name="url">The address to read.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The document, or <see langword="null"/> when it could not be read.</returns>
    private async Task<XDocument?> ReadXmlAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) ||
            !await _robots.IsAllowedAsync(address, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("robots.txt disallows {Address}; not reading it.", url);
            return null;
        }

        try
        {
            using var response = await client.GetAsync(address, cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Handlers usually decompress transparently, but a .gz served as a
            // plain octet-stream arrives compressed and has to be unwrapped here.
            var compressed = url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
                             response.Content.Headers.ContentEncoding.Count == 0;

            if (!compressed)
            {
                return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using var decompressed = new GZipStream(stream, CompressionMode.Decompress);

            return await XDocument.LoadAsync(decompressed, LoadOptions.None, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the sitemap at {Address}.", url);
            return null;
        }
    }

    /// <summary>One game page listed in a sitemap.</summary>
    /// <param name="Slug">The game's identifier within the site.</param>
    /// <param name="LastModified">When the site says the page last changed.</param>
    private sealed record SitemapEntry(string Slug, DateTimeOffset? LastModified);
}
