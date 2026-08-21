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
    /// Asks every adapter that handles one of the listing's source pages.
    /// </summary>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Every address found, ranked, or the most informative refusal.</returns>
    /// <remarks>
    /// <para>
    /// All of them, not the first that answers, and their addresses are merged
    /// into one ranked list rather than one winning. A download that fails
    /// halfway is the ordinary case for the hosts this launcher fetches from,
    /// and the transfer only survives it if the next mirror is already on the
    /// row. Discarding the other adapters' answers threw away exactly the
    /// alternates that make an install resilient.
    /// </para>
    /// <para>
    /// Asked concurrently, because they are independent network calls against
    /// different hosts and the aggregate needs all of them regardless. Ordering
    /// is decided afterwards from the priorities, never from which answered
    /// first — a mirror list that reshuffled itself according to network weather
    /// would make a failing install impossible to reason about.
    /// </para>
    /// <para>
    /// Every adapter is asked even when an earlier one succeeded, which costs a
    /// request that the old short circuit saved. That is the price of the
    /// fallback list, and it is only paid by listings whose own downloads are
    /// missing — the common path returns before this is ever called.
    /// </para>
    /// </remarks>
    private async Task<SourcingPayload> AskAdaptersAsync(
        CatalogListing listing,
        CancellationToken cancellationToken)
    {
        var records = await _listings
            .GetSourceListingsAsync(listing.ListingId, cancellationToken)
            .ConfigureAwait(false);

        var asked = new List<Task<AdapterAnswer>>();

        foreach (var record in records)
        {
            var url = record.SourceUrl.AbsoluteUri;

            for (var index = 0; index < _adapters.Count; index++)
            {
                var adapter = _adapters[index];

                if (adapter.CanHandle(url))
                {
                    asked.Add(AskOneAsync(adapter, index, listing, url, cancellationToken));
                }
            }
        }

        if (asked.Count == 0)
        {
            return SourcingPayload.Unsupported;
        }

        var answers = await Task.WhenAll(asked).ConfigureAwait(false);

        // Priority first, then registration order. Registration order is what
        // puts a hand-written feed ahead of a built-in that asked for the same
        // number, and it is stable, so the same catalogue produces the same
        // mirror list on every machine.
        var ranked = answers
            .OrderByDescending(answer => answer.Priority)
            .ThenBy(answer => answer.Index)
            .ToArray();

        var downloads = Merge(ranked);

        if (downloads.Count > 0)
        {
            _logger.LogInformation(
                "Sourcing '{Title}' produced {Count} address(es) from {Adapters} adapter(s).",
                listing.Title,
                downloads.Count,
                ranked.Count(answer => answer.Payload is { HasDownloads: true }));

            return new SourcingPayload(downloads);
        }

        // Nothing was found, so the best available explanation is what is left to
        // report. "I do not handle this address" is the absence of one, and
        // letting it win would hide a real refusal from an adapter that does.
        var explained = ranked.FirstOrDefault(answer =>
            answer.Payload is { } payload &&
            payload.Refusal is not (SourcingRefusal.None or SourcingRefusal.Unsupported));

        return explained?.Payload ?? SourcingPayload.Unsupported;
    }

    /// <summary>
    /// Merges every adapter's addresses into one ranked list.
    /// </summary>
    /// <param name="answers">The answers, already in the order they should rank.</param>
    /// <returns>The merged rows, renumbered from zero.</returns>
    /// <remarks>
    /// <para>
    /// Duplicates are dropped rather than kept, and the first occurrence wins —
    /// which, given the ordering, is the highest-priority adapter that offered
    /// it. Two adapters describing the same host frequently produce the same
    /// address; keeping both would have aria2c retry a URL that just failed and
    /// call it a fallback.
    /// </para>
    /// <para>
    /// <see cref="ListingDownload.MirrorRank"/> is renumbered across the whole
    /// merged list. Each adapter numbers its own rows from zero, so leaving them
    /// alone would give several rows the same rank and lose the ordering this
    /// method exists to establish.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ListingDownload> Merge(IReadOnlyList<AdapterAnswer> answers)
    {
        var merged = new List<ListingDownload>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var answer in answers)
        {
            if (answer.Payload is not { HasDownloads: true } payload)
            {
                continue;
            }

            foreach (var download in payload.Downloads)
            {
                if (string.IsNullOrWhiteSpace(download.Url) || !seen.Add(download.Url))
                {
                    continue;
                }

                download.MirrorRank = merged.Count;
                merged.Add(download);
            }
        }

        return merged;
    }

    /// <summary>
    /// Asks one adapter, turning a failure into an answer rather than a throw.
    /// </summary>
    /// <param name="adapter">The adapter to ask.</param>
    /// <param name="index">Its registration position, used to break priority ties.</param>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="url">The page address it claimed.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What it said.</returns>
    /// <remarks>
    /// One adapter failing must not stop the others, for the same reason a
    /// throwing achievement provider does not stop a pass. With the calls now
    /// running together that matters more, not less: <c>Task.WhenAll</c> surfaces
    /// one exception and abandons the rest of the results, so a single
    /// unreachable host would otherwise lose every mirror found alongside it.
    /// </remarks>
    private async Task<AdapterAnswer> AskOneAsync(
        ISourcingAdapter adapter,
        int index,
        CatalogListing listing,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await adapter
                .ExtractDownloadPayloadAsync(listing, url, cancellationToken)
                .ConfigureAwait(false);

            return new AdapterAnswer(payload.Priority ?? adapter.Priority, index, payload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Adapter} failed for '{Title}'.", adapter.DisplayName, listing.Title);

            return new AdapterAnswer(
                adapter.Priority,
                index,
                new SourcingPayload([], SourcingRefusal.Unreachable, ex.Message));
        }
    }

    /// <summary>What one adapter said, with what it takes to rank it.</summary>
    /// <param name="Priority">Effective priority, the payload's own or the adapter's.</param>
    /// <param name="Index">Registration position, breaking ties between equal priorities.</param>
    /// <param name="Payload">What it produced.</param>
    private sealed record AdapterAnswer(int Priority, int Index, SourcingPayload? Payload);

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
