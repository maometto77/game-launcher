using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Artwork;

/// <summary>
/// Fetches artwork from SteamGridDB.
/// </summary>
/// <remarks>
/// <para>
/// Chosen because it indexes artwork by game rather than by store listing, so it
/// covers console-only and long-delisted titles that a Steam-derived source would
/// not have. It needs a free API key, which the user supplies in settings; the
/// launcher never ships one.
/// </para>
/// <para>
/// Only PNG and JPEG candidates are returned. SteamGridDB also serves WebP, which
/// WPF's imaging stack cannot decode — a WebP would download successfully and then
/// render as nothing at all, which is a far more confusing failure than simply not
/// finding artwork.
/// </para>
/// </remarks>
public sealed class SteamGridDbArtworkProvider : IArtworkProvider
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for artwork requests.</summary>
    public const string HttpClientName = "artwork";

    private const string ApiRoot = "https://www.steamgriddb.com/api/v2";

    /// <summary>Extensions WPF can actually decode.</summary>
    private static readonly string[] RenderableExtensions = [".png", ".jpg", ".jpeg"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<SteamGridDbArtworkProvider> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured artwork client.</param>
    /// <param name="settings">Supplies the API key.</param>
    /// <param name="logger">Logger for provider diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SteamGridDbArtworkProvider(
        IHttpClientFactory httpClientFactory,
        ISettingsService settings,
        ILogger<SteamGridDbArtworkProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string DisplayName => "SteamGridDB";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.SteamGridDbApiKey);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var payload = await SendAsync<SearchResponse>(
            $"{ApiRoot}/search/autocomplete/{Uri.EscapeDataString(title.Trim())}",
            cancellationToken).ConfigureAwait(false);

        if (payload?.Data is not { Count: > 0 } matches)
        {
            return [];
        }

        return matches
            .Where(match => match.Id > 0 && !string.IsNullOrWhiteSpace(match.Name))
            .Select(match => new ArtworkGameMatch(match.Id, match.Name!))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetCandidatesAsync(
        int providerGameId,
        ArtworkKind kind,
        CancellationToken cancellationToken = default)
    {
        // Dimensions are requested rather than filtered afterwards, so the
        // response is already the right shape for where the image is used: a
        // portrait tile, or a wide banner behind a title.
        var (segment, dimensions) = kind switch
        {
            ArtworkKind.Hero => ("heroes", "1920x620,3840x1240"),
            _ => ("grids", "600x900,342x482")
        };

        var payload = await SendAsync<AssetResponse>(
            $"{ApiRoot}/{segment}/game/{providerGameId}?dimensions={dimensions}&types=static&nsfw=false&humor=false",
            cancellationToken).ConfigureAwait(false);

        if (payload?.Data is not { Count: > 0 } assets)
        {
            return [];
        }

        return assets
            .Where(asset => asset.Url is not null && IsRenderable(asset.Url))
            .Select(asset => new ArtworkCandidate(
                kind,
                new Uri(asset.Url!, UriKind.Absolute),
                asset.Width,
                asset.Height,
                asset.Score))

            // The provider's own score first, then the largest image, so a tile
            // never ends up upscaled when a bigger one was available.
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Width * candidate.Height)
            .ToArray();
    }

    /// <summary>
    /// Issues an authenticated GET and deserialises the response.
    /// </summary>
    /// <typeparam name="T">Expected payload type.</typeparam>
    /// <param name="url">Absolute request address.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The payload, or <see langword="null"/> when the game is simply not known.</returns>
    /// <exception cref="InvalidOperationException">No API key is configured, or the key was rejected.</exception>
    /// <exception cref="HttpRequestException">The request failed.</exception>
    private async Task<T?> SendAsync<T>(string url, CancellationToken cancellationToken)
        where T : class
    {
        var apiKey = _settings.Current.SteamGridDbApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No SteamGridDB API key is configured. Add one on the Settings page.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // A game with no artwork of this kind is a normal answer, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "SteamGridDB rejected the API key. Check it on the Settings page.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await JsonSerializer
                .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SteamGridDB returned a response that could not be read.");
            return null;
        }
    }

    /// <summary>Determines whether an image URL points at a format WPF can decode.</summary>
    /// <param name="url">The candidate address.</param>
    /// <returns><see langword="true"/> when the extension is renderable.</returns>
    private static bool IsRenderable(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var extension = Path.GetExtension(parsed.AbsolutePath);
        return RenderableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Search response envelope.</summary>
    private sealed class SearchResponse
    {
        /// <summary>Matched games.</summary>
        public List<SearchMatch>? Data { get; set; }
    }

    /// <summary>One matched game.</summary>
    private sealed class SearchMatch
    {
        /// <summary>SteamGridDB's identifier.</summary>
        public int Id { get; set; }

        /// <summary>Canonical name.</summary>
        public string? Name { get; set; }
    }

    /// <summary>Asset response envelope.</summary>
    private sealed class AssetResponse
    {
        /// <summary>Available assets.</summary>
        public List<Asset>? Data { get; set; }
    }

    /// <summary>One image asset.</summary>
    private sealed class Asset
    {
        /// <summary>Full-size image address.</summary>
        public string? Url { get; set; }

        /// <summary>Pixel width.</summary>
        public int Width { get; set; }

        /// <summary>Pixel height.</summary>
        public int Height { get; set; }

        /// <summary>Community score.</summary>
        [JsonPropertyName("score")]
        public int Score { get; set; }
    }
}
