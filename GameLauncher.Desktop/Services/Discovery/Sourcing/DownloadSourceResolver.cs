using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing;

/// <summary>
/// Finds somewhere a listing's game can actually be fetched from.
/// </summary>
/// <remarks>
/// A listing may be well described by a source that cannot supply the file. This
/// is what turns that into a working install rather than a dead end: the same
/// game, described elsewhere in the catalogue, usually can be downloaded.
/// </remarks>
public interface IDownloadSourceResolver
{
    /// <summary>
    /// Works out what can be downloaded for a listing.
    /// </summary>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The payload, or a refusal explaining why there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="listing"/> is <see langword="null"/>.</exception>
    Task<SourcingPayload> ResolveAsync(
        CatalogListing listing,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IDownloadSourceResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three steps, cheapest first. The listing's own downloads, then whatever its
/// sourcing adapters can produce, and finally the same game found elsewhere in
/// the catalogue.
/// </para>
/// <para>
/// The last step is what makes a metadata-only source worth having. A game that
/// MyAbandonware describes and the Internet Archive also holds is installable
/// through the Archive and better described because of MyAbandonware — which is
/// the multi-source design paying for itself rather than a workaround.
/// </para>
/// </remarks>
public sealed class DownloadSourceResolver : IDownloadSourceResolver
{
    private readonly IReadOnlyList<ISourcingAdapter> _adapters;
    private readonly ICatalogListingRepository _listings;
    private readonly IListingNormalizer _normalizer;
    private readonly ILogger<DownloadSourceResolver> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="adapters">Site adapters that may produce a payload.</param>
    /// <param name="listings">Used to find the same game described elsewhere.</param>
    /// <param name="normalizer">Computes the title key two listings must share.</param>
    /// <param name="logger">Logger for sourcing diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DownloadSourceResolver(
        IEnumerable<ISourcingAdapter> adapters,
        ICatalogListingRepository listings,
        IListingNormalizer normalizer,
        ILogger<DownloadSourceResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _listings = listings ?? throw new ArgumentNullException(nameof(listings));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _adapters = adapters.ToArray();
    }

    /// <inheritdoc />
    public async Task<SourcingPayload> ResolveAsync(
        CatalogListing listing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);

        // The ordinary case: the merge already unioned every source's addresses
        // onto this row.
        //
        // A restricted listing is excluded even when it has addresses. Its
        // source has said the item may be looked at but not taken away, so those
        // addresses answer 403 and using them would turn a clear explanation
        // into a failed download.
        if (listing.IsDownloadable && listing.Downloads.Count > 0)
        {
            return new SourcingPayload(listing.Downloads);
        }

        var refusal = await AskAdaptersAsync(listing, cancellationToken).ConfigureAwait(false);

        if (refusal.HasDownloads)
        {
            return refusal;
        }

        var elsewhere = await FindElsewhereAsync(listing, cancellationToken).ConfigureAwait(false);

        if (elsewhere is not null)
        {
            _logger.LogInformation(
                "'{Title}' has no download of its own; using the copy described by {Source}.",
                listing.Title, elsewhere.PrimarySourceKey);

            return new SourcingPayload(elsewhere.Downloads);
        }

        // Nothing worked. An adapter's explanation is more useful than a generic
        // one because it says *why* — but only when it actually says something.
        // "No adapter handles that address" is the absence of an explanation,
        // and letting it win would hide the caller's better one.
        return refusal.Refusal is SourcingRefusal.None or SourcingRefusal.Unsupported
            ? new SourcingPayload([], SourcingRefusal.NoPayload)
            : refusal;
    }

    /// <summary>
    /// Asks each adapter that handles one of the listing's source pages.
    /// </summary>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The first payload produced, or the most informative refusal.</returns>
    private async Task<SourcingPayload> AskAdaptersAsync(
        CatalogListing listing,
        CancellationToken cancellationToken)
    {
        var records = await _listings
            .GetSourceListingsAsync(listing.ListingId, cancellationToken)
            .ConfigureAwait(false);

        SourcingPayload? refusal = null;

        foreach (var record in records)
        {
            var url = record.SourceUrl.AbsoluteUri;
            var adapter = _adapters.FirstOrDefault(candidate => candidate.CanHandle(url));

            if (adapter is null)
            {
                continue;
            }

            SourcingPayload payload;

            try
            {
                payload = await adapter
                    .ExtractDownloadPayloadAsync(listing, url, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One adapter failing must not stop the others, for the same
                // reason a throwing achievement provider does not stop a pass.
                _logger.LogWarning(ex, "{Adapter} failed for '{Title}'.", adapter.DisplayName, listing.Title);

                refusal ??= new SourcingPayload([], SourcingRefusal.Unreachable, ex.Message);
                continue;
            }

            if (payload.HasDownloads)
            {
                return payload;
            }

            // Kept so the caller can be told why, rather than only that nothing
            // was found. The first refusal wins: it is the listing's own source.
            refusal ??= payload;
        }

        return refusal ?? SourcingPayload.Unsupported;
    }

    /// <summary>
    /// Looks for the same game described by another listing that has a download.
    /// </summary>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The other listing, fully loaded, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Matched on the normalised title, which is the same key the importer uses
    /// to decide two observations are the same game. A year that disagrees by
    /// more than one is not the same release and is left alone — the same rule
    /// that governs merging, applied to the same question.
    /// </remarks>
    private async Task<CatalogListing?> FindElsewhereAsync(
        CatalogListing listing,
        CancellationToken cancellationToken)
    {
        var titleKey = _normalizer.ComputeTitleKey(listing.Title);

        if (titleKey.Length == 0)
        {
            return null;
        }

        var candidates = await _listings
            .FindByTitleKeyAsync(titleKey, cancellationToken)
            .ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.ListingId, listing.ListingId, StringComparison.Ordinal) ||
                !candidate.IsDownloadable ||
                !IsSameRelease(listing.Year, candidate.Year))
            {
                continue;
            }

            // FindByTitleKey does not populate collections, so the candidate has
            // to be loaded properly before its downloads can be read.
            var loaded = await _listings.GetAsync(candidate.ListingId, cancellationToken)
                .ConfigureAwait(false);

            if (loaded is { Downloads.Count: > 0 })
            {
                return loaded;
            }
        }

        return null;
    }

    /// <summary>Determines whether two years can describe the same release.</summary>
    /// <param name="left">One year.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when they are compatible.</returns>
    private static bool IsSameRelease(int? left, int? right) =>
        left is null || right is null || Math.Abs(left.Value - right.Value) <= 1;
}
