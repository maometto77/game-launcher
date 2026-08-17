namespace GameLauncher.Desktop.Models;

/// <summary>
/// How much of a source an import pass should cover.
/// </summary>
public enum ImportMode
{
    /// <summary>
    /// Only items whose change stamp beats the stored one. The default, and what
    /// the background service runs.
    /// </summary>
    Incremental = 0,

    /// <summary>Re-fetch everything, ignoring stored watermarks.</summary>
    Full = 1,

    /// <summary>
    /// Re-normalise and re-merge stored payloads without contacting any source.
    /// </summary>
    /// <remarks>
    /// The mode used when a normalisation or merge rule changes. It applies the
    /// new rules to the whole catalogue in seconds, offline, which is only
    /// possible because raw payloads are kept.
    /// </remarks>
    Remerge = 2
}

/// <summary>
/// Bookkeeping for one import pass over one source.
/// </summary>
/// <remarks>
/// Holds the resume cursor, so a pass killed halfway continues rather than
/// starting over, and the parse success rate, which is how a silently broken
/// parser is detected.
/// </remarks>
public sealed class CatalogImportRun
{
    /// <summary>Auto-incrementing primary key.</summary>
    public long RunId { get; set; }

    /// <summary>Source this pass covered.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>How much of the source the pass covered.</summary>
    public ImportMode Mode { get; set; }

    /// <summary>When the pass started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the pass finished, or <see langword="null"/> while running.</summary>
    /// <remarks>
    /// A row with no completion time on startup is the residue of a process that
    /// was killed mid-pass. It is closed out and its cursor reused, for the same
    /// reason an unfinished play session is closed out crediting zero time.
    /// </remarks>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Opaque resume token, or <see langword="null"/> when the pass completed.</summary>
    public string? Cursor { get; set; }

    /// <summary>References enumerated.</summary>
    public int ItemsSeen { get; set; }

    /// <summary>Items fetched and found to have changed.</summary>
    public int ItemsChanged { get; set; }

    /// <summary>Items that could not be fetched or parsed.</summary>
    public int ItemsFailed { get; set; }

    /// <summary>Listings created for the first time.</summary>
    public int ListingsAdded { get; set; }

    /// <summary>The last error seen, or <see langword="null"/>.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets the fraction of fetched items that yielded a usable listing.
    /// </summary>
    /// <remarks>
    /// The signal that a scraper has broken. A site redesign turns a working
    /// parser into one that returns empty records, and without this the run looks
    /// like a success that simply found nothing new.
    /// </remarks>
    public double ParseSuccessRate
    {
        get
        {
            var attempted = ItemsChanged + ItemsFailed;
            return attempted == 0 ? 1d : (double)ItemsChanged / attempted;
        }
    }
}
