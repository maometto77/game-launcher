using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// What happened when a game was imported.
/// </summary>
public enum GameImportStatus
{
    /// <summary>The game was added to the library.</summary>
    Added = 0,

    /// <summary>An entry with the same executable path already existed; nothing was changed.</summary>
    AlreadyInLibrary = 1,

    /// <summary>The import failed. See the accompanying message.</summary>
    Failed = 2
}

/// <summary>
/// The outcome of importing one executable.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Game">
/// The resulting game for <see cref="GameImportStatus.Added"/>, the existing
/// entry for <see cref="GameImportStatus.AlreadyInLibrary"/>, and
/// <see langword="null"/> on failure.
/// </param>
/// <param name="Message">A user-facing explanation, or <see langword="null"/> on success.</param>
public sealed record GameImportResult(GameImportStatus Status, Game? Game, string? Message);

/// <summary>
/// Describes a game to add to the library.
/// </summary>
/// <remarks>
/// Every field except the executable path is optional. Anything left unset is
/// derived from the executable itself, which is what makes "pick an exe and
/// press Add" a complete flow.
/// </remarks>
public sealed record GameImportRequest
{
    /// <summary>Absolute path to the executable to launch.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Display title, or <see langword="null"/> to derive one from the executable.</summary>
    public string? Title { get; init; }

    /// <summary>Install folder, or <see langword="null"/> to infer it from the executable's location.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>Tags to apply.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Collection to file the game under, or <see langword="null"/> to leave it uncollected.</summary>
    public int? CollectionId { get; init; }

    /// <summary>The URL the game was downloaded from, when it came from "Install from URL".</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Whether to use the executable's embedded icon as placeholder cover art.</summary>
    public bool ExtractIcon { get; init; } = true;

    /// <summary>
    /// Whether to measure the install folder's size on import.
    /// </summary>
    /// <remarks>
    /// Walking a large installation costs seconds, so a bulk import can switch it
    /// off and let the size be filled in later rather than making the user wait
    /// through dozens of folder walks.
    /// </remarks>
    public bool MeasureInstallSize { get; init; } = true;
}

/// <summary>
/// Adds games to the library from executables on disk.
/// </summary>
public interface IGameImportService
{
    /// <summary>
    /// Adds one game to the library, deriving whatever the request leaves unset.
    /// </summary>
    /// <param name="request">What to import.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    Task<GameImportResult> ImportAsync(GameImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds several games, continuing past individual failures.
    /// </summary>
    /// <param name="requests">What to import.</param>
    /// <param name="progress">Optional receiver reporting each completed import.</param>
    /// <param name="cancellationToken">Cancels the remaining imports.</param>
    /// <returns>One result per request, in the order supplied.</returns>
    /// <remarks>
    /// One unreadable executable in a batch of thirty must not abandon the other
    /// twenty-nine, so failures are reported per item rather than thrown.
    /// </remarks>
    Task<IReadOnlyList<GameImportResult>> ImportManyAsync(
        IReadOnlyCollection<GameImportRequest> requests,
        IProgress<GameImportResult>? progress = null,
        CancellationToken cancellationToken = default);
}
