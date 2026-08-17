using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Discovery.Matching;

/// <summary>
/// One candidate a merge rule considered, and whether it won.
/// </summary>
/// <param name="Field">The field being resolved.</param>
/// <param name="SourceKey">The source that offered this value.</param>
/// <param name="Value">The value offered, rendered as text.</param>
/// <param name="Won">Whether this candidate was selected.</param>
/// <param name="Rule">The rule that made the decision.</param>
/// <remarks>
/// Captured only when explicitly asked for. It is the layer that makes a merge
/// rule debuggable — re-running the merge over stored payloads with capture
/// enabled reconstructs exactly why a field holds what it holds, offline.
/// </remarks>
public sealed record MergeTraceEntry(
    string Field,
    string SourceKey,
    string? Value,
    bool Won,
    string Rule);

/// <summary>
/// The merged listing and the record of how it was arrived at.
/// </summary>
/// <param name="Listing">The merged row.</param>
/// <param name="FieldProvenance">Which source won each scalar field.</param>
/// <param name="Trace">
/// Every candidate every rule considered, or <see langword="null"/> when capture
/// was not requested.
/// </param>
public sealed record MergeResult(
    CatalogListing Listing,
    IReadOnlyDictionary<string, string> FieldProvenance,
    IReadOnlyList<MergeTraceEntry>? Trace);

/// <summary>
/// Collapses every source's view of one game into the row the catalogue shows.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and a total function of its inputs: merging the same observations twice
/// produces the same row. That is what makes the merged table derived rather than
/// authoritative, and what makes it safe to rebuild the whole catalogue from
/// stored payloads after a rule changes.
/// </para>
/// <para>
/// The policy in one sentence: <em>scalar fields resolve by per-field precedence;
/// collection fields union.</em> And a null never overwrites a value — a source
/// that omits a field abstains rather than voting for empty, which is what stops
/// a degraded parser from hollowing out good data.
/// </para>
/// </remarks>
public interface IListingMerger
{
    /// <summary>
    /// Merges every observation of one game.
    /// </summary>
    /// <param name="listingId">Identity to assign to the merged row.</param>
    /// <param name="sources">Every source's view, in any order.</param>
    /// <param name="captureTrace">
    /// Whether to record each rule's candidates. Off in the normal path, because
    /// the trace is several times the size of the row it explains.
    /// </param>
    /// <returns>The merged row and its provenance.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sources"/> is empty.</exception>
    MergeResult Merge(
        string listingId,
        IReadOnlyList<SourceListing> sources,
        bool captureTrace = false);
}
