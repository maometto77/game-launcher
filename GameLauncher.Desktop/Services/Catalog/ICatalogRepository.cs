using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Catalog;

/// <summary>
/// Persistence for the local view of the shared game catalog.
/// </summary>
public interface ICatalogRepository
{
    /// <summary>Gets every catalog entry known locally, including superseded ones.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>All entries.</returns>
    Task<IReadOnlyList<CatalogEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets one entry by its catalog identity, without following merge redirects.</summary>
    /// <param name="catalogId">The identity to look up.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The entry, or <see langword="null"/> when it is unknown.</returns>
    Task<CatalogEntry?> GetByIdAsync(string catalogId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Follows merge redirects to the surviving entry.
    /// </summary>
    /// <param name="catalogId">Any catalog identity, current or superseded.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The canonical entry, or <see langword="null"/> when the identity is unknown.</returns>
    /// <remarks>
    /// An identity is never rewritten once assigned, so a client may hold one that
    /// has since been merged away. Resolution walks the chain rather than assuming
    /// a single hop, and is bounded so a cycle introduced by a faulty relay cannot
    /// hang the caller.
    /// </remarks>
    Task<CatalogEntry?> ResolveCanonicalAsync(string catalogId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the canonical entry a fingerprint resolves to.
    /// </summary>
    /// <param name="fingerprint">The fingerprint to look up.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching canonical entry, or <see langword="null"/>.</returns>
    Task<CatalogEntry?> FindByFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Gets the entries that still carry a locally minted identity.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Entries awaiting promotion by a relay.</returns>
    Task<IReadOnlyList<CatalogEntry>> GetProvisionalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a catalog entry, recording its originating fingerprint as the
    /// first alias.
    /// </summary>
    /// <param name="entry">The entry to insert.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the rows have been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    Task AddAsync(CatalogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Updates an entry's mutable fields. The identity is never changed.</summary>
    /// <param name="entry">The entry to update.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> if a row was updated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    Task<bool> UpdateAsync(CatalogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a fingerprint resolves to a catalog entry.
    /// </summary>
    /// <param name="fingerprint">The fingerprint.</param>
    /// <param name="catalogId">The entry it resolves to.</param>
    /// <param name="source">The authority recording the alias.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> when a new alias was recorded.</returns>
    /// <remarks>
    /// Existing aliases are left alone. A fingerprint already bound to a title
    /// must not be silently rebound by a later observation; that is a merge, and
    /// merges are explicit.
    /// </remarks>
    Task<bool> AddAliasAsync(
        string fingerprint,
        string catalogId,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>Gets every fingerprint that resolves to an entry.</summary>
    /// <param name="catalogId">The entry to list aliases for.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The entry's aliases.</returns>
    Task<IReadOnlyList<CatalogAlias>> GetAliasesAsync(
        string catalogId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a provisional identity with the one a relay assigned.
    /// </summary>
    /// <param name="provisionalCatalogId">The locally minted identity being retired.</param>
    /// <param name="assignedCatalogId">The identity the relay assigned.</param>
    /// <param name="source">The relay that assigned it.</param>
    /// <param name="canonicalTitle">The relay's title for the entry.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>
    /// <see langword="true"/> when the entry was promoted in place;
    /// <see langword="false"/> when <paramref name="assignedCatalogId"/> already
    /// existed locally, in which case the caller must merge the two.
    /// </returns>
    /// <remarks>
    /// This is the one place a catalog identity is rewritten, and it applies only
    /// to a provisional id — one this client minted and no relay has ever seen. An
    /// identity that has been <em>assigned</em> is immutable from that moment on;
    /// unifying two assigned identities is <see cref="MergeIntoAsync"/>, which
    /// moves references instead.
    /// </remarks>
    Task<bool> PromoteAsync(
        string provisionalCatalogId,
        string assignedCatalogId,
        string source,
        string canonicalTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds one catalog entry into another by moving every reference, leaving
    /// the absorbed entry behind as a redirect.
    /// </summary>
    /// <param name="sourceCatalogId">The entry being absorbed.</param>
    /// <param name="targetCatalogId">The entry that survives.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The number of rows repointed.</returns>
    /// <remarks>
    /// <para>
    /// No identity is rewritten and nothing the user earned is discarded. Where
    /// both entries define an achievement with the same api name, the surviving
    /// definition inherits the earlier unlock and the higher progress of the two
    /// before the duplicate is removed.
    /// </para>
    /// <para>
    /// The absorbed entry is kept with
    /// <see cref="CatalogEntry.SupersededByCatalogId"/> set, so a client or relay
    /// still holding the old identity resolves to the survivor rather than
    /// finding nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Either identifier is null or blank.</exception>
    Task<int> MergeIntoAsync(
        string sourceCatalogId,
        string targetCatalogId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns entries assigned by a different relay to provisional state, so
    /// they will be re-resolved against the current one.
    /// </summary>
    /// <param name="currentRelaySource">Instance identity of the relay now in use.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The number of entries demoted.</returns>
    /// <remarks>
    /// <para>
    /// A catalog id is only meaningful within the relay that issued it. Pointing
    /// the launcher at a different relay makes every previously assigned id
    /// meaningless there, and continuing to use one would attach this user's
    /// achievements to whatever unrelated title happens to hold that id on the
    /// new relay.
    /// </para>
    /// <para>
    /// Demotion is the exact mirror of promotion: a fresh provisional id is
    /// written over the old one and <c>ON UPDATE CASCADE</c> carries every game,
    /// achievement definition, stat and alias across. Nothing local is deleted or
    /// rewritten — only the identity changes, and the fingerprint that follows it
    /// is what lets the new relay resolve it.
    /// </para>
    /// <para>
    /// Idempotent: a demoted entry's source is local, so a second pass finds
    /// nothing to do.
    /// </para>
    /// </remarks>
    Task<int> DemoteForeignEntriesAsync(
        string currentRelaySource,
        CancellationToken cancellationToken = default);
}
