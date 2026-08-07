using System.Net.Http;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Artwork;

/// <summary>
/// Default <see cref="IArtworkService"/>.
/// </summary>
public sealed class ArtworkService : IArtworkService
{
    private const int BufferSize = 64 * 1024;

    /// <summary>Largest image accepted, as a guard against a mistyped URL serving something huge.</summary>
    private const long MaxImageBytes = 32 * 1024 * 1024;

    private readonly IArtworkProvider _provider;
    private readonly IGameRepository _games;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppPaths _paths;
    private readonly ILogger<ArtworkService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="provider">Supplies artwork candidates.</param>
    /// <param name="games">Persists the resulting paths.</param>
    /// <param name="httpClientFactory">Supplies the client used to download images.</param>
    /// <param name="paths">Resolves the artwork folder.</param>
    /// <param name="logger">Logger for artwork diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ArtworkService(
        IArtworkProvider provider,
        IGameRepository games,
        IHttpClientFactory httpClientFactory,
        IAppPaths paths,
        ILogger<ArtworkService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsConfigured => _provider.IsConfigured;

    /// <inheritdoc />
    public string ProviderName => _provider.DisplayName;

    /// <inheritdoc />
    public async Task<ArtworkResult> ApplyArtworkAsync(
        Game game,
        string? searchTitle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        var title = string.IsNullOrWhiteSpace(searchTitle) ? game.Title : searchTitle;

        var matches = await _provider.SearchAsync(title, cancellationToken).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return new ArtworkResult(null, null, null, $"{ProviderName} has no game called '{title}'.");
        }

        // The first match is the provider's own best guess. Choosing differently
        // is the caller's business — the details page offers the search box for
        // exactly that.
        var match = matches[0];

        var cover = await TryApplyAsync(game, match, ArtworkKind.Cover, cancellationToken).ConfigureAwait(false);
        var hero = await TryApplyAsync(game, match, ArtworkKind.Hero, cancellationToken).ConfigureAwait(false);

        if (cover is null && hero is null)
        {
            return new ArtworkResult(
                match.Name, null, null, $"'{match.Name}' was found, but it has no usable artwork.");
        }

        game.CoverArtPath = cover ?? game.CoverArtPath;
        game.HeroArtPath = hero ?? game.HeroArtPath;

        await _games.UpdateAsync(game, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Applied artwork for {Title} from {Provider} as '{Match}' (cover={Cover}, hero={Hero}).",
            game.Title, ProviderName, match.Name, cover is not null, hero is not null);

        var found = (cover, hero) switch
        {
            (not null, not null) => "cover and background",
            (not null, null) => "cover only",
            _ => "background only"
        };

        return new ArtworkResult(match.Name, cover, hero, $"Matched '{match.Name}' — {found}.");
    }

    /// <summary>
    /// Downloads the best candidate of one kind, if there is one.
    /// </summary>
    /// <param name="game">The game being illustrated.</param>
    /// <param name="match">The matched provider game.</param>
    /// <param name="kind">Which image to fetch.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The saved path, or <see langword="null"/> when nothing usable was found.</returns>
    /// <remarks>
    /// One kind failing never stops the other. A game with a cover and no banner
    /// is a normal outcome, and refusing both because one was missing would be
    /// worse than partial artwork.
    /// </remarks>
    private async Task<string?> TryApplyAsync(
        Game game,
        ArtworkGameMatch match,
        ArtworkKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await _provider
                .GetCandidatesAsync(match.ProviderGameId, kind, cancellationToken)
                .ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                return null;
            }

            return await DownloadAsync(game, candidates[0], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // A rejected key is worth surfacing rather than silently producing no
            // artwork, so it travels up.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fetching {Kind} artwork for {Title} failed.", kind, game.Title);
            return null;
        }
    }

    /// <summary>
    /// Downloads a candidate into the artwork folder.
    /// </summary>
    /// <param name="game">The game being illustrated.</param>
    /// <param name="candidate">The image to fetch.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The saved path.</returns>
    /// <remarks>
    /// The file name is derived from the game's own identity and the kind, never
    /// from the remote URL: a name chosen by a remote server has no business
    /// deciding what gets written to disk, and a stable name means re-fetching
    /// artwork replaces the old image instead of accumulating copies.
    /// </remarks>
    private async Task<string> DownloadAsync(
        Game game,
        ArtworkCandidate candidate,
        CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(game.GlobalKey)
            ? game.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : game.GlobalKey;

        var extension = Path.GetExtension(candidate.Url.AbsolutePath);
        if (string.IsNullOrEmpty(extension) || extension.Length > 5)
        {
            extension = ".png";
        }

        var suffix = candidate.Kind == ArtworkKind.Hero ? "hero" : "cover";
        var path = Path.Combine(_paths.ArtworkDirectory, $"{key}-{suffix}{extension}");

        Directory.CreateDirectory(_paths.ArtworkDirectory);

        var client = _httpClientFactory.CreateClient(SteamGridDbArtworkProvider.HttpClientName);

        using var response = await client
            .GetAsync(candidate.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxImageBytes)
        {
            throw new InvalidOperationException(
                $"The image at {candidate.Url} is larger than the {MaxImageBytes / (1024 * 1024)} MB limit.");
        }

        // Written to a temporary file and moved into place, so a failed download
        // never leaves a half-written image where the library expects a whole one.
        var temporary = path + ".part";

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);

        return path;
    }
}
