using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// Options controlling a folder scan.
/// </summary>
/// <param name="MaximumDepth">
/// How many directory levels below the root to descend. Bounded so that pointing
/// the scanner at a drive root cannot walk an entire file system.
/// </param>
/// <param name="MinimumExecutableBytes">
/// Executables smaller than this are listed but not pre-selected. Small binaries
/// beside a game are almost always helpers, patchers or stubs.
/// </param>
/// <param name="MaximumResults">
/// Upper bound on candidates returned, so a badly chosen root produces a long
/// list rather than an unusable one.
/// </param>
public sealed record ScanOptions(
    int MaximumDepth = 6,
    long MinimumExecutableBytes = 128 * 1024,
    int MaximumResults = 500)
{
    /// <summary>Gets the options used when the caller does not specify any.</summary>
    public static ScanOptions Default { get; } = new();
}

/// <summary>
/// Discovers candidate games by walking a folder tree.
/// </summary>
public interface IGameScanService
{
    /// <summary>
    /// Recursively searches a folder for executables that could be games.
    /// </summary>
    /// <param name="rootDirectory">Folder to search.</param>
    /// <param name="options">Limits applied to the walk.</param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>
    /// The candidates found, most promising first. Never adds anything to the
    /// library.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is null or blank.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    Task<IReadOnlyList<DiscoveredGame>> ScanAsync(
        string rootDirectory,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
