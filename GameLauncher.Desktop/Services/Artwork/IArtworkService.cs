using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Artwork;

/// <summary>
/// What an artwork lookup produced.
/// </summary>
/// <param name="MatchedName">
/// The provider's name for the game it matched, or <see langword="null"/> when
/// nothing matched.
/// </param>
/// <param name="CoverPath">Path to the downloaded cover, or <see langword="null"/>.</param>
/// <param name="HeroPath">Path to the downloaded hero banner, or <see langword="null"/>.</param>
/// <param name="Message">A line of text describing the outcome, always populated.</param>
public sealed record ArtworkResult(
    string? MatchedName,
    string? CoverPath,
    string? HeroPath,
    string Message)
{
    /// <summary>Gets a value indicating whether anything was downloaded.</summary>
    public bool FoundAnything => CoverPath is not null || HeroPath is not null;
}

/// <summary>
/// Finds and applies cover and hero artwork for a game.
/// </summary>
/// <remarks>
/// Downloads into the launcher's artwork folder and updates the library row, so
/// artwork survives the source going away and the game being moved. Nothing here
/// is required for the launcher to work: a game with no artwork renders a
/// generated tile from its initials.
/// </remarks>
public interface IArtworkService
{
    /// <summary>Gets a value indicating whether an artwork provider is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Gets the configured provider's name, for reporting.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Finds the best artwork for a game and applies it.
    /// </summary>
    /// <param name="game">The game to illustrate. Its artwork paths are updated in place.</param>
    /// <param name="searchTitle">
    /// Title to search for, or <see langword="null"/> to use the game's own title.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>What was found and applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No provider is configured, or its key was rejected.</exception>
    Task<ArtworkResult> ApplyArtworkAsync(
        Game game,
        string? searchTitle = null,
        CancellationToken cancellationToken = default);
}
