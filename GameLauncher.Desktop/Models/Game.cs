using System.IO;

namespace GameLauncher.Desktop.Models;

/// <summary>
/// A single title in the local library.
/// </summary>
/// <remarks>
/// This is the launcher's own record of a game. It never represents ownership
/// or licensing — it is simply a pointer to an executable on disk plus the
/// metadata and statistics the launcher has accumulated about it.
/// </remarks>
public sealed class Game
{
    /// <summary>Auto-incrementing primary key. Zero for a game not yet persisted.</summary>
    /// <remarks>
    /// Local to this database. Never transmitted — see <see cref="GlobalKey"/>.
    /// </remarks>
    public int Id { get; set; }

    /// <summary>
    /// Stable identity for <em>this installation's row</em>, as 32 lowercase
    /// hexadecimal characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Installation-local, and deliberately not a cross-user identifier: two
    /// people who own the same game generate unrelated values. Its remaining job
    /// is to survive local renumbering — export and re-import, or a rebuild that
    /// reassigns <see cref="Id"/> — and to give logs something stable to name a
    /// row by.
    /// </para>
    /// <para>
    /// For anything shared between users or synchronised to a relay, use
    /// <see cref="CatalogId"/>.
    /// </para>
    /// </remarks>
    public string GlobalKey { get; set; } = string.Empty;

    /// <summary>
    /// The shared catalog identity of the title this installation is a copy of,
    /// or <see langword="null"/> before one has been assigned.
    /// </summary>
    /// <remarks>
    /// The identifier every cross-user feature keys on. See
    /// <see cref="CatalogEntry"/> for how provisional identities are promoted to
    /// server-assigned ones.
    /// </remarks>
    public string? CatalogId { get; set; }

    /// <summary>Display title shown throughout the UI.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the portrait cover image, or <see langword="null"/> to
    /// fall back to generated artwork.
    /// </summary>
    public string? CoverArtPath { get; set; }

    /// <summary>
    /// Absolute path to the wide hero banner shown on the details page, or
    /// <see langword="null"/> when none has been supplied.
    /// </summary>
    public string? HeroArtPath { get; set; }

    /// <summary>Absolute path to the executable launched by the Play button.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Root folder of the installation. Used as the working directory on launch
    /// and as the deletion target when uninstalling with "delete files".
    /// </summary>
    public string? InstallDir { get; set; }

    /// <summary>Size of the install folder in bytes, measured when the game was added or rescanned.</summary>
    public long InstallSizeBytes { get; set; }

    /// <summary>Total accumulated playtime in seconds across every recorded session.</summary>
    public long PlaytimeSeconds { get; set; }

    /// <summary>When the game was last launched, or <see langword="null"/> if never played.</summary>
    public DateTimeOffset? LastPlayedAt { get; set; }

    /// <summary>When the game was added to the library.</summary>
    public DateTimeOffset DateAdded { get; set; }

    /// <summary>
    /// Free-form user tags used for filtering and search.
    /// </summary>
    /// <remarks>
    /// Persisted as a JSON array in a single column rather than a join table.
    /// Tags are always read and written as a complete set alongside the game, so
    /// a separate table would add joins without buying anything, and JSON keeps
    /// tags containing commas or semicolons safe.
    /// </remarks>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Identifier of the owning collection, or <see langword="null"/> when the
    /// game is uncollected.
    /// </summary>
    public int? CollectionId { get; set; }

    /// <summary>User's free-text notes about this game.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// The URL this game was installed from, when it was added through
    /// "Install from URL". Retained so the user can see the provenance of an
    /// installed title.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// When this record was last modified locally.
    /// </summary>
    /// <remarks>
    /// Exists for conflict resolution once records can be synchronised. Playtime
    /// accrues on whichever machine the game was played on, so a future merge has
    /// to be able to tell which side's copy is newer rather than assuming one
    /// wins.
    /// </remarks>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Gets a value indicating whether the recorded executable currently exists
    /// on disk.
    /// </summary>
    /// <remarks>
    /// Touches the file system on every call, so it is meant for occasional
    /// checks (details page, pre-launch validation) rather than per-item binding
    /// in a list. The library view model snapshots this instead of binding to it
    /// directly.
    /// </remarks>
    public bool ExecutableExists =>
        !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);
}
