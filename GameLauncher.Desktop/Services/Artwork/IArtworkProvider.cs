namespace GameLauncher.Desktop.Services.Artwork;

/// <summary>
/// The kind of image being fetched.
/// </summary>
public enum ArtworkKind
{
    /// <summary>Portrait cover art for a library tile, around 600×900.</summary>
    Cover = 0,

    /// <summary>Wide banner for the top of a game's details page, around 1920×620.</summary>
    Hero = 1
}

/// <summary>
/// A game the provider believes matches a search.
/// </summary>
/// <param name="ProviderGameId">The provider's own identifier for the game.</param>
/// <param name="Name">The provider's canonical name for it.</param>
public sealed record ArtworkGameMatch(int ProviderGameId, string Name);

/// <summary>
/// One image the provider can supply.
/// </summary>
/// <param name="Kind">What the image is for.</param>
/// <param name="Url">Direct address of the full-size image.</param>
/// <param name="Width">Pixel width, or zero when the provider did not say.</param>
/// <param name="Height">Pixel height, or zero when the provider did not say.</param>
/// <param name="Score">The provider's own popularity ranking; higher is better.</param>
public sealed record ArtworkCandidate(ArtworkKind Kind, Uri Url, int Width, int Height, int Score);

/// <summary>
/// Supplies cover and hero images for a game.
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a direct dependency on any one site, so a second source can
/// be added without touching the service that downloads and applies the result.
/// </para>
/// <para>
/// Providers only find and describe images. Downloading them, deciding where they
/// live on disk, and updating the library are the artwork service's job — for the
/// same reason achievement providers only decide and never persist.
/// </para>
/// </remarks>
public interface IArtworkProvider
{
    /// <summary>Human-readable name, shown when reporting what was used.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets a value indicating whether the provider has the configuration it
    /// needs — typically an API key.
    /// </summary>
    /// <remarks>
    /// An unconfigured provider is not an error. Artwork is an enhancement, and
    /// the launcher shows generated tiles perfectly well without it.
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>
    /// Finds games matching a title.
    /// </summary>
    /// <param name="title">Title to search for.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Matches, best first. Empty when nothing matched.</returns>
    /// <exception cref="InvalidOperationException">The provider is not configured.</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">The provider could not be reached.</exception>
    Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the images available for a matched game.
    /// </summary>
    /// <param name="providerGameId">Identifier from a <see cref="ArtworkGameMatch"/>.</param>
    /// <param name="kind">Which kind of image to list.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Candidates, best first. Empty when the provider has none.</returns>
    Task<IReadOnlyList<ArtworkCandidate>> GetCandidatesAsync(
        int providerGameId,
        ArtworkKind kind,
        CancellationToken cancellationToken = default);
}
