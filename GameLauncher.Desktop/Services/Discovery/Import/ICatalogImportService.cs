using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Discovery.Import;

/// <summary>
/// Narrows what an import pass does.
/// </summary>
public sealed record ImportRunOptions
{
    /// <summary>Sources to run, or <see langword="null"/> for every available one.</summary>
    public IReadOnlyList<string>? SourceKeys { get; init; }

    /// <summary>How much of each source to cover.</summary>
    public ImportMode Mode { get; init; } = ImportMode.Incremental;

    /// <summary>Stop after this many items per source. Zero means no limit.</summary>
    public int MaxItems { get; init; }

    /// <summary>
    /// Whether to record how each merge rule reached its decision.
    /// </summary>
    /// <remarks>
    /// Off in the normal path. Turning it on and re-running in
    /// <see cref="ImportMode.Remerge"/> reconstructs, offline, exactly why every
    /// field holds what it holds.
    /// </remarks>
    public bool CaptureMergeTrace { get; init; }
}

/// <summary>
/// How far an import pass has got.
/// </summary>
/// <param name="SourceKey">The source being imported.</param>
/// <param name="ItemsSeen">References enumerated so far.</param>
/// <param name="ItemsChanged">Items fetched and found to have changed.</param>
/// <param name="Message">A line of text describing what is happening.</param>
public sealed record ImportProgress(string SourceKey, int ItemsSeen, int ItemsChanged, string Message);

/// <summary>
/// What one source's pass produced.
/// </summary>
/// <param name="SourceKey">The source.</param>
/// <param name="DisplayName">Its human-readable name.</param>
/// <param name="ItemsSeen">References enumerated.</param>
/// <param name="ItemsChanged">Items fetched and found to have changed.</param>
/// <param name="ItemsFailed">Items that could not be fetched or parsed.</param>
/// <param name="ListingsAdded">Listings created for the first time.</param>
/// <param name="Aborted">Whether the pass stopped early because too much failed.</param>
/// <param name="Error">The failure that ended the pass, or <see langword="null"/>.</param>
public sealed record ImportSourceResult(
    string SourceKey,
    string DisplayName,
    int ItemsSeen,
    int ItemsChanged,
    int ItemsFailed,
    int ListingsAdded,
    bool Aborted,
    string? Error);

/// <summary>
/// What a whole import pass produced.
/// </summary>
/// <param name="Sources">One result per source that ran.</param>
public sealed record ImportRunResult(IReadOnlyList<ImportSourceResult> Sources)
{
    /// <summary>Gets how many listings were created across every source.</summary>
    public int ListingsAdded => Sources.Sum(source => source.ListingsAdded);

    /// <summary>Gets how many items changed across every source.</summary>
    public int ItemsChanged => Sources.Sum(source => source.ItemsChanged);

    /// <summary>Gets a value indicating whether anything at all changed.</summary>
    public bool HasChanges => ItemsChanged > 0 || ListingsAdded > 0;
}

/// <summary>
/// Raised when an import pass changed the catalogue.
/// </summary>
/// <param name="listingsAdded">How many listings were created.</param>
/// <param name="listingsUpdated">How many existing listings changed.</param>
public sealed class CatalogUpdatedEventArgs(int listingsAdded, int listingsUpdated) : EventArgs
{
    /// <summary>Gets how many listings were created.</summary>
    public int ListingsAdded { get; } = listingsAdded;

    /// <summary>Gets how many existing listings changed.</summary>
    public int ListingsUpdated { get; } = listingsUpdated;
}

/// <summary>
/// Drives sources, matches what they return against the catalogue, and persists
/// the result.
/// </summary>
/// <remarks>
/// The only component that touches more than one layer, which is deliberate:
/// sources stay pure fetchers, matching and merging stay pure functions, and the
/// orchestration that has to know about all of them lives in exactly one place.
/// </remarks>
public interface ICatalogImportService
{
    /// <summary>Gets the registered sources, whether or not they are available.</summary>
    IReadOnlyList<ICatalogSource> Sources { get; }

    /// <summary>Raised after a pass that changed the catalogue.</summary>
    /// <remarks>
    /// Raised on the thread the import ran on. Subscribers that touch the
    /// interface must marshal, which is what <c>IUiDispatcher</c> is for.
    /// </remarks>
    event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated;

    /// <summary>Gets a value indicating whether a pass is running.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Runs an import pass.
    /// </summary>
    /// <param name="options">What to import.</param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the pass, leaving it resumable.</param>
    /// <returns>What each source produced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A pass is already running.</exception>
    Task<ImportRunResult> RunAsync(
        ImportRunOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
