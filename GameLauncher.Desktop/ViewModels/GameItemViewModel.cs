using System.Globalization;
using GameLauncher.Desktop.Helpers;
using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// Presents one <see cref="Game"/> for display in the library.
/// </summary>
/// <remarks>
/// <para>
/// A thin, immutable projection created once per load. Values that would
/// otherwise touch the file system or be recomputed on every binding pass —
/// notably whether the executable still exists — are snapshotted here, so
/// scrolling a large library does not turn into a storm of disk checks.
/// </para>
/// <para>
/// Deliberately not observable: nothing on it changes in place. When a game is
/// edited the library reloads and builds fresh items, which keeps the list and
/// the database from drifting apart.
/// </para>
/// </remarks>
public sealed class GameItemViewModel
{
    /// <summary>
    /// Initialises a new instance from a persisted game.
    /// </summary>
    /// <param name="game">The game to present.</param>
    /// <param name="collectionName">Name of the owning collection, if any.</param>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> is <see langword="null"/>.</exception>
    public GameItemViewModel(Game game, string? collectionName = null)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        CollectionName = collectionName;

        // Snapshotted deliberately; see the class remarks.
        IsExecutableMissing = !game.ExecutableExists;

        Initials = BuildInitials(game.Title);
        PlaytimeText = PlaytimeConverter.Format(game.PlaytimeSeconds);
        InstallSizeText = ByteSizeConverter.Format(game.InstallSizeBytes);
        LastPlayedText = game.LastPlayedAt is { } played
            ? RelativeTimeConverter.Format(played)
            : "Never played";
        TagsText = game.Tags.Count > 0 ? string.Join(", ", game.Tags) : string.Empty;
    }

    /// <summary>Gets the underlying game record.</summary>
    public Game Game { get; }

    /// <summary>Gets the game's identifier.</summary>
    public int Id => Game.Id;

    /// <summary>Gets the game's title.</summary>
    public string Title => Game.Title;

    /// <summary>Gets the cover art path, or <see langword="null"/> to use the fallback tile.</summary>
    public string? CoverArtPath => Game.CoverArtPath;

    /// <summary>Gets the name of the owning collection, or <see langword="null"/> when uncollected.</summary>
    public string? CollectionName { get; }

    /// <summary>
    /// Gets up to two letters derived from the title, used to draw a readable
    /// tile when the game has no cover art.
    /// </summary>
    public string Initials { get; }

    /// <summary>Gets the formatted total playtime.</summary>
    public string PlaytimeText { get; }

    /// <summary>Gets the formatted install size.</summary>
    public string InstallSizeText { get; }

    /// <summary>Gets a relative description of when the game was last played.</summary>
    public string LastPlayedText { get; }

    /// <summary>Gets the game's tags as a comma-separated list, or an empty string.</summary>
    public string TagsText { get; }

    /// <summary>
    /// Gets a value indicating whether the recorded executable was missing when
    /// this item was built.
    /// </summary>
    public bool IsExecutableMissing { get; }

    /// <summary>Gets a value indicating whether the game has ever been played.</summary>
    public bool HasBeenPlayed => Game.PlaytimeSeconds > 0;

    /// <summary>
    /// Builds display initials from a title.
    /// </summary>
    /// <param name="title">The game's title.</param>
    /// <returns>One or two upper-case letters, or <c>?</c> when none can be derived.</returns>
    private static string BuildInitials(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "?";
        }

        // Letters and digits only, so a title like "[Beta] Game" does not produce
        // a tile reading "[G".
        var words = title
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.FirstOrDefault(char.IsLetterOrDigit))
            .Where(character => character != default)
            .Take(2)
            .ToArray();

        return words.Length == 0
            ? "?"
            : new string(words).ToUpper(CultureInfo.CurrentCulture);
    }
}
