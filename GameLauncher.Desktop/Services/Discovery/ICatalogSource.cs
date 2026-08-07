namespace GameLauncher.Desktop.Services.Discovery;

/// <summary>
/// Narrows what an enumeration pass returns.
/// </summary>
public sealed record SourceEnumerationOptions
{
    /// <summary>
    /// Only items changed since this point, where the source can express it.
    /// </summary>
    /// <remarks>
    /// A source that cannot filter by change time ignores this and relies on the
    /// pipeline's content-hash short circuit instead.
    /// </remarks>
    public DateTimeOffset? ChangedSince { get; init; }

    /// <summary>Opaque resume token from the previous pass, or <see langword="null"/> to start over.</summary>
    public string? Cursor { get; init; }

    /// <summary>Stop after this many references. Zero means no limit.</summary>
    public int MaxItems { get; init; }
}

/// <summary>
/// A cheap reference to one item, obtained without the expensive fetch.
/// </summary>
/// <param name="SourceKey">Dispatch key of the source that produced this.</param>
/// <param name="SourceItemId">The source's own identifier.</param>
/// <param name="Title">Title as listed, used only for logging and progress.</param>
/// <param name="SourceUpdatedAt">When the source last changed it, or <see langword="null"/>.</param>
/// <param name="Cursor">Resume token positioned after this item, or <see langword="null"/>.</param>
/// <remarks>
/// Separating this from <see cref="SourceListing"/> is what makes incremental
/// imports cheap: the pipeline can decide to skip an item from its reference
/// alone, without ever making the request that would return its metadata.
/// </remarks>
public sealed record SourceListingRef(
    string SourceKey,
    string SourceItemId,
    string Title,
    DateTimeOffset? SourceUpdatedAt,
    string? Cursor);

/// <summary>
/// One external site the discovery catalogue can be populated from.
/// </summary>
/// <remarks>
/// <para>
/// A source finds and describes. It does not persist, does not download images,
/// does not decide that two listings are the same game, and never touches the
/// database — for the same reason achievement providers only decide and artwork
/// providers only describe. That keeps every source testable with no database
/// and no library, against a captured payload.
/// </para>
/// <para>
/// Sources are registered as an open set and dispatched by <see cref="Key"/>, so
/// adding one is a class and a container registration.
/// </para>
/// </remarks>
public interface ICatalogSource
{
    /// <summary>
    /// Dispatch key, stored on every row this source contributes.
    /// </summary>
    /// <remarks>
    /// Declare it as a <c>public const string SourceKey</c> on the implementation
    /// so definitions and tests can name it without a magic string.
    /// </remarks>
    string Key { get; }

    /// <summary>Human-readable name, shown when reporting what was imported.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Relative trust when two sources disagree; lower wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A whole-source ranking, used only where the per-field rules leave a tie.
    /// The per-field rules come first because no source is better at everything —
    /// one may have the cleanest titles and the other the most reliable
    /// developer credits.
    /// </para>
    /// <para>
    /// Declared here rather than in the merger so that adding a source does not
    /// mean editing the merge logic, and so the merger never has to name a
    /// concrete source.
    /// </para>
    /// </remarks>
    int Rank { get; }

    /// <summary>
    /// Gets a value indicating whether the source can currently be used.
    /// </summary>
    /// <remarks>
    /// False when configuration is missing or when the site's own rules disallow
    /// automated access. An unavailable source is skipped and logged, never
    /// treated as a failure — the catalogue works with whichever sources are
    /// available.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Lists what the source holds, cheaply, yielding as it pages.
    /// </summary>
    /// <param name="options">Narrows the pass.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>References, streamed so the caller can checkpoint mid-pass.</returns>
    /// <remarks>
    /// Streaming rather than returning a list is deliberate: a source with
    /// several thousand items would otherwise have to be walked to the end before
    /// the first item could be imported, and a cancelled pass would lose all of
    /// it.
    /// </remarks>
    IAsyncEnumerable<SourceListingRef> EnumerateAsync(
        SourceEnumerationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches full metadata for one item.
    /// </summary>
    /// <param name="reference">The item to fetch, from <see cref="EnumerateAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>
    /// The listing, or <see langword="null"/> when the item no longer exists or
    /// is not a game.
    /// </returns>
    /// <exception cref="System.Net.Http.HttpRequestException">The source could not be reached.</exception>
    /// <remarks>
    /// Returning <see langword="null"/> for "not applicable" and throwing only
    /// for transport failure is what lets the pipeline distinguish an item to
    /// skip permanently from one to retry later.
    /// </remarks>
    Task<SourceListing?> FetchAsync(
        SourceListingRef reference,
        CancellationToken cancellationToken = default);
}
