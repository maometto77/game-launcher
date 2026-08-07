using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// How a catalogue query orders its results.
/// </summary>
public enum CatalogListingSort
{
    /// <summary>
    /// Search relevance, falling back to title when there is no search text.
    /// </summary>
    Relevance = 0,

    /// <summary>Alphabetical by sort title.</summary>
    Title = 1,

    /// <summary>Newest release first.</summary>
    YearDescending = 2,

    /// <summary>Oldest release first.</summary>
    YearAscending = 3,

    /// <summary>Most recently added to the catalogue first.</summary>
    RecentlyAdded = 4
}

/// <summary>
/// A page of catalogue results.
/// </summary>
public sealed record CatalogListingQuery
{
    /// <summary>Free text to search for, or <see langword="null"/> to browse.</summary>
    /// <remarks>
    /// Matched through the full-text index rather than with <c>LIKE</c>, so it
    /// ranks and stays fast as the catalogue grows.
    /// </remarks>
    public string? SearchText { get; init; }

    /// <summary>Restrict to one genre, or <see langword="null"/>.</summary>
    public string? Genre { get; init; }

    /// <summary>Restrict to one platform, or <see langword="null"/>.</summary>
    public string? Platform { get; init; }

    /// <summary>Restrict to one developer, or <see langword="null"/>.</summary>
    public string? Developer { get; init; }

    /// <summary>Restrict to one publisher, or <see langword="null"/>.</summary>
    public string? Publisher { get; init; }

    /// <summary>Earliest release year to include, or <see langword="null"/>.</summary>
    public int? YearFrom { get; init; }

    /// <summary>Latest release year to include, or <see langword="null"/>.</summary>
    public int? YearTo { get; init; }

    /// <summary>Whether to exclude listings with nothing to download.</summary>
    public bool DownloadableOnly { get; init; }

    /// <summary>Whether to include listings the user has hidden.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>How to order the results.</summary>
    public CatalogListingSort Sort { get; init; } = CatalogListingSort.Relevance;

    /// <summary>How many results to skip.</summary>
    public int Skip { get; init; }

    /// <summary>How many results to return.</summary>
    /// <remarks>
    /// Paging is not optional. A catalogue of several thousand listings must
    /// never be loaded whole into an observable collection, however tempting the
    /// simpler code is.
    /// </remarks>
    public int Take { get; init; } = 60;
}

/// <summary>
/// One page of results and the size of the full result set.
/// </summary>
/// <param name="Items">The listings on this page.</param>
/// <param name="TotalCount">How many listings match, ignoring paging.</param>
public sealed record CatalogListingPage(IReadOnlyList<CatalogListing> Items, int TotalCount);

/// <summary>
/// One facet value and how many listings carry it.
/// </summary>
/// <param name="Name">The value.</param>
/// <param name="Count">How many listings have it.</param>
public sealed record CatalogFacet(string Name, int Count);

/// <summary>
/// The facet values available for filtering.
/// </summary>
/// <param name="Genres">Genres, most common first.</param>
/// <param name="Platforms">Platforms, most common first.</param>
/// <param name="Developers">Developers, most common first.</param>
/// <param name="Publishers">Publishers, most common first.</param>
public sealed record CatalogFacets(
    IReadOnlyList<CatalogFacet> Genres,
    IReadOnlyList<CatalogFacet> Platforms,
    IReadOnlyList<CatalogFacet> Developers,
    IReadOnlyList<CatalogFacet> Publishers);

/// <summary>
/// Persistence for the discovery catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="Catalog.ICatalogRepository"/>, which owns shared catalog
/// <em>identity</em> for installed games. The two aggregates share nothing but a
/// nullable column on <see cref="Game"/>.
/// </para>
/// <para>
/// This repository owns the only write path into the catalogue, which is why the
/// search index is maintained here inside the same transaction rather than by
/// triggers: there is exactly one caller to keep honest, and a trigger that had
/// to aggregate across three join tables would be harder to reason about and
/// impossible to test in isolation.
/// </para>
/// </remarks>
public interface ICatalogListingRepository
{
    /// <summary>
    /// Gets one listing with every collection populated.
    /// </summary>
    /// <param name="listingId">The listing to load.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The listing, or <see langword="null"/> when unknown.</returns>
    Task<CatalogListing?> GetAsync(string listingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the listings whose normalised title matches, for duplicate detection.
    /// </summary>
    /// <param name="titleKey">The normalised title to look for.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Candidate listings, without their collections populated.</returns>
    /// <remarks>
    /// Narrowing by title before matching is what keeps duplicate detection from
    /// comparing every observation against the whole catalogue.
    /// </remarks>
    Task<IReadOnlyList<CatalogListing>> FindByTitleKeyAsync(
        string titleKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a match key through the alias table.
    /// </summary>
    /// <param name="matchKey">The key to resolve.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The listing the key is bound to, or <see langword="null"/>.</returns>
    Task<string?> ResolveAliasAsync(string matchKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds a match key to a listing, leaving an existing binding alone.
    /// </summary>
    /// <param name="matchKey">The key to bind.</param>
    /// <param name="listingId">The listing it resolves to.</param>
    /// <param name="source">What recorded the alias.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns><see langword="true"/> when a new alias was recorded.</returns>
    Task<bool> AddAliasAsync(
        string matchKey,
        string listingId,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches and filters the catalogue.
    /// </summary>
    /// <param name="query">What to return.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>One page of results and the total match count.</returns>
    Task<CatalogListingPage> QueryAsync(
        CatalogListingQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the facet values available for filtering.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Facets, most common first.</returns>
    Task<CatalogFacets> GetFacetsAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts the listings in the catalogue.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The number of listings.</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a batch of merged listings in one transaction.
    /// </summary>
    /// <param name="listings">The listings to write.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>How many rows were inserted or changed.</returns>
    /// <remarks>
    /// <para>
    /// Batched deliberately. Every other repository here opens a connection per
    /// call, which is right for interactive use and wrong for an import writing
    /// thousands of rows — one transaction per row would dominate the run.
    /// </para>
    /// <para>
    /// Cached image paths are carried across an update. Re-importing must not
    /// discard artwork that has already been fetched simply because the metadata
    /// was refreshed.
    /// </para>
    /// </remarks>
    Task<int> UpsertManyAsync(
        IReadOnlyList<CatalogListing> listings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one source observation.
    /// </summary>
    /// <param name="sourceKey">The source.</param>
    /// <param name="sourceItemId">The source's identifier for the item.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The observation, or <see langword="null"/> when it is new.</returns>
    Task<ListingSourceRecord?> GetSourceAsync(
        string sourceKey,
        string sourceItemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a source observation, replacing any previous one.
    /// </summary>
    /// <param name="record">The observation to store.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task UpsertSourceAsync(ListingSourceRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets every source's normalised view of one listing, for merging.
    /// </summary>
    /// <param name="listingId">The listing.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Every observation, deserialised.</returns>
    Task<IReadOnlyList<SourceListing>> GetSourceListingsAsync(
        string listingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the identifiers of every listing that has at least one observation
    /// from a source.
    /// </summary>
    /// <param name="sourceKey">The source, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Listing identifiers.</returns>
    /// <remarks>Drives a re-merge, which never contacts the network.</remarks>
    Task<IReadOnlyList<string>> GetListingIdsWithSourcesAsync(
        string? sourceKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the local path of a cached image.
    /// </summary>
    /// <param name="listingId">The listing.</param>
    /// <param name="remoteUrl">The image's remote address.</param>
    /// <param name="localPath">Where it was cached.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task SetImagePathAsync(
        string listingId,
        string remoteUrl,
        string localPath,
        CancellationToken cancellationToken = default);

    /// <summary>Starts an import run and returns its identifier.</summary>
    /// <param name="sourceKey">The source being imported.</param>
    /// <param name="mode">How much of it the pass covers.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The new run's identifier.</returns>
    Task<long> StartRunAsync(
        string sourceKey,
        ImportMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>Saves a run's progress so it can resume after a kill.</summary>
    /// <param name="run">The run, with its counters and cursor.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task CheckpointRunAsync(CatalogImportRun run, CancellationToken cancellationToken = default);

    /// <summary>Marks a run finished.</summary>
    /// <param name="run">The run, with its final counters.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    Task CompleteRunAsync(CatalogImportRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recent run for a source, finished or not.
    /// </summary>
    /// <param name="sourceKey">The source.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The run, or <see langword="null"/> when the source has never run.</returns>
    Task<CatalogImportRun?> GetLastRunAsync(
        string sourceKey,
        CancellationToken cancellationToken = default);
}
