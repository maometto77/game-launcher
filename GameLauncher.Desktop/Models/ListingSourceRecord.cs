namespace GameLauncher.Desktop.Models;

/// <summary>
/// What one source said about one listing, stored as the evidence behind the
/// merged row.
/// </summary>
/// <remarks>
/// <para>
/// These rows are the authoritative input to the merge;
/// <see cref="CatalogListing"/> is derived from them. Keeping them means a
/// normalisation or merge-rule change can be applied to the whole catalogue by
/// re-running the merge over stored data, with no network access at all.
/// </para>
/// <para>
/// It is also the only way to answer "why does this listing say 1993?" after the
/// fact, and the only way to diagnose a parser that produced a wrong answer
/// rather than no answer.
/// </para>
/// </remarks>
public sealed class ListingSourceRecord
{
    /// <summary>Listing this observation contributes to.</summary>
    public string ListingId { get; set; } = string.Empty;

    /// <summary>Dispatch key of the source.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>The source's own identifier for the item.</summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>Human-visible page this came from, kept for attribution.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>The normalised observation, serialised as JSON.</summary>
    /// <remarks>
    /// Stored alongside <see cref="RawPayload"/> rather than derived from it on
    /// every merge, because re-parsing several thousand payloads to answer a
    /// query would make the common path pay for the rare one.
    /// </remarks>
    public string NormalizedJson { get; set; } = string.Empty;

    /// <summary>The unmodified payload the source returned, gzip-compressed.</summary>
    public byte[]? RawPayload { get; set; }

    /// <summary>When the source last changed the item, or <see langword="null"/>.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; set; }

    /// <summary>When this observation was last fetched.</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>
    /// Hash of the normalised observation, used to skip work when a re-fetch
    /// returned the same thing.
    /// </summary>
    public string SourceContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Precedence of this source when merging, lower winning ties.
    /// </summary>
    /// <remarks>
    /// A whole-record rank, used only to break ties the per-field rules leave
    /// open. The per-field rules come first, because no source is better at
    /// everything.
    /// </remarks>
    public int Rank { get; set; }

    /// <summary>
    /// The last error seen fetching this item, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A permanent failure is recorded rather than retried forever. The row
    /// survives so the item is not re-discovered and re-attempted on every pass.
    /// </remarks>
    public string? LastError { get; set; }
}
