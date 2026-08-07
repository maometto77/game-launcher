using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery.Matching;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Import;

/// <summary>
/// Default <see cref="ICatalogImportService"/>.
/// </summary>
public sealed class CatalogImportService : ICatalogImportService
{
    /// <summary>How many items are fetched before results are written and progress checkpointed.</summary>
    /// <remarks>
    /// A compromise. Larger batches amortise the transaction over more rows;
    /// smaller ones lose less work when the process is killed and let the user
    /// see progress sooner.
    /// </remarks>
    private const int BatchSize = 100;

    /// <summary>Fraction of fetched items that must parse before a pass is considered healthy.</summary>
    private const double MinimumParseSuccessRate = 0.8;

    /// <summary>
    /// How many items must be attempted before the health check can fire.
    /// </summary>
    /// <remarks>
    /// Without a floor, the first two items failing would abort a pass over
    /// several thousand — and the first items of a crawl are exactly where an
    /// odd one out is most likely.
    /// </remarks>
    private const int HealthCheckSampleSize = 25;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<ICatalogSource> _sources;
    private readonly ICatalogListingRepository _repository;
    private readonly IListingNormalizer _normalizer;
    private readonly IListingMatcher _matcher;
    private readonly IListingMerger _merger;
    private readonly ILogger<CatalogImportService> _logger;

    private int _running;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="sources">Every registered source.</param>
    /// <param name="repository">Persists listings and observations.</param>
    /// <param name="normalizer">Normalises what sources return.</param>
    /// <param name="matcher">Decides which listing an observation belongs to.</param>
    /// <param name="merger">Collapses observations into a listing.</param>
    /// <param name="logger">Logger for import diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Two sources share a key.</exception>
    public CatalogImportService(
        IEnumerable<ICatalogSource> sources,
        ICatalogListingRepository repository,
        IListingNormalizer normalizer,
        IListingMatcher matcher,
        IListingMerger merger,
        ILogger<CatalogImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _sources = sources.ToArray();

        // Failing at construction is deliberate, and matches the achievement
        // engine. Two sources quietly sharing a key would mean rows attributed to
        // whichever won, which is far harder to diagnose than a startup error.
        var duplicate = _sources
            .GroupBy(source => source.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"More than one catalogue source is registered with the key '{duplicate.Key}'.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ICatalogSource> Sources => _sources;

    /// <inheritdoc />
    public event EventHandler<CatalogUpdatedEventArgs>? CatalogUpdated;

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <inheritdoc />
    public async Task<ImportRunResult> RunAsync(
        ImportRunOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // A compare-and-swap rather than a lock: two passes writing the same rows
        // would each see half of the other's work, and the second would spend its
        // whole run re-fetching what the first was already fetching.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A catalogue import is already running.");
        }

        try
        {
            var results = new List<ImportSourceResult>();

            var selected = _sources
                .Where(source => options.SourceKeys is null ||
                                 options.SourceKeys.Contains(source.Key, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (options.Mode == ImportMode.Remerge)
            {
                results.Add(await RemergeAsync(options, progress, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                // Sequential across sources on purpose. Each has its own politeness
                // budget, and running them together would make the slowest source's
                // spacing the whole pass's pace for no gain.
                foreach (var source in selected)
                {
                    if (!source.IsAvailable)
                    {
                        _logger.LogInformation(
                            "Skipping {Source}: it is not available.", source.DisplayName);

                        continue;
                    }

                    results.Add(await ImportSourceAsync(source, options, progress, cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            var outcome = new ImportRunResult(results);

            if (outcome.HasChanges)
            {
                CatalogUpdated?.Invoke(
                    this,
                    new CatalogUpdatedEventArgs(
                        outcome.ListingsAdded, outcome.ItemsChanged - outcome.ListingsAdded));
            }

            return outcome;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Runs one source's pass.
    /// </summary>
    /// <param name="source">The source to import.</param>
    /// <param name="options">What to import.</param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the source produced.</returns>
    private async Task<ImportSourceResult> ImportSourceAsync(
        ICatalogSource source,
        ImportRunOptions options,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var previous = await _repository.GetLastRunAsync(source.Key, cancellationToken).ConfigureAwait(false);

        // An unfinished previous run is the residue of a process killed mid-pass.
        // Its cursor is exactly where to carry on from, which is the whole reason
        // it was checkpointed.
        var resumeCursor = previous is { CompletedAt: null } ? previous.Cursor : null;

        // A completed previous run's start time is the watermark: anything the
        // source changed after we began looking is worth looking at again.
        DateTimeOffset? changedSince = null;

        if (options.Mode != ImportMode.Full && previous is { CompletedAt: not null })
        {
            changedSince = previous.StartedAt;
        }

        var runId = await _repository.StartRunAsync(source.Key, options.Mode, cancellationToken)
            .ConfigureAwait(false);

        var run = new CatalogImportRun { RunId = runId, SourceKey = source.Key, Mode = options.Mode };

        using var throttle = new RequestThrottle(source.Throttle);

        var batch = new List<SourceListingRef>(BatchSize);

        // Tracked separately from run.ItemsFailed, which also counts items the
        // matcher declined to place. An ambiguous title says nothing about
        // whether the parser still works, and letting it trip the health check
        // would abort a perfectly good pass over a catalogue full of remakes.
        var fetchFailures = 0;
        var attempted = 0;
        var aborted = false;
        string? error = null;

        try
        {
            var enumeration = source.EnumerateAsync(
                new SourceEnumerationOptions
                {
                    ChangedSince = changedSince,
                    Cursor = resumeCursor,
                    MaxItems = options.MaxItems
                },
                cancellationToken);

            await foreach (var reference in enumeration.WithCancellation(cancellationToken))
            {
                run.ItemsSeen++;
                batch.Add(reference);

                if (batch.Count < BatchSize)
                {
                    continue;
                }

                fetchFailures += await ProcessBatchAsync(
                    source, throttle, batch, options, run, cancellationToken).ConfigureAwait(false);

                attempted += batch.Count;
                run.Cursor = batch[^1].Cursor;
                batch.Clear();

                await _repository.CheckpointRunAsync(run, cancellationToken).ConfigureAwait(false);

                progress?.Report(new ImportProgress(
                    source.Key, run.ItemsSeen, run.ItemsChanged,
                    $"{source.DisplayName}: {run.ItemsSeen} seen, {run.ItemsChanged} updated"));

                if (IsUnhealthy(attempted, fetchFailures))
                {
                    aborted = true;
                    error = DescribeCollapse(source, attempted, fetchFailures);
                    _logger.LogError("{Error}", error);
                    break;
                }

                if (options.MaxItems > 0 && run.ItemsSeen >= options.MaxItems)
                {
                    break;
                }
            }

            if (!aborted && batch.Count > 0)
            {
                fetchFailures += await ProcessBatchAsync(
                    source, throttle, batch, options, run, cancellationToken).ConfigureAwait(false);

                attempted += batch.Count;
                run.Cursor = batch[^1].Cursor;

                // Checked here too, not only inside the loop. A source holding
                // fewer items than one batch would otherwise never be health
                // checked at all, and a completely broken parser would report a
                // clean run that simply found nothing.
                if (IsUnhealthy(attempted, fetchFailures))
                {
                    aborted = true;
                    error = DescribeCollapse(source, attempted, fetchFailures);
                    _logger.LogError("{Error}", error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Leave the run open. Its cursor is where the next pass resumes, which
            // is the point of checkpointing at all.
            await _repository.CheckpointRunAsync(run, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            run.LastError = ex.Message;
            _logger.LogError(ex, "Importing {Source} failed.", source.DisplayName);
        }

        if (!aborted)
        {
            run.Cursor = null;
        }

        run.LastError = error;

        await _repository.CompleteRunAsync(run, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "{Source}: {Seen} seen, {Changed} changed, {Added} new, {Failed} failed.",
            source.DisplayName, run.ItemsSeen, run.ItemsChanged, run.ListingsAdded, run.ItemsFailed);

        return new ImportSourceResult(
            source.Key, source.DisplayName, run.ItemsSeen, run.ItemsChanged,
            run.ItemsFailed, run.ListingsAdded, aborted, error);
    }

    /// <summary>
    /// Fetches a batch concurrently, then places and persists it sequentially.
    /// </summary>
    /// <param name="source">The source being imported.</param>
    /// <param name="throttle">Limits how hard the source is hit.</param>
    /// <param name="batch">References to process.</param>
    /// <param name="options">What to import.</param>
    /// <param name="run">Counters for the pass.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    /// <returns>How many items could not be fetched or parsed.</returns>
    /// <remarks>
    /// The split matters. Fetching is network-bound and parallel; placing is
    /// database-bound and strictly sequential, because two new observations of the
    /// same game arriving together must not each mint their own listing.
    /// </remarks>
    private async Task<int> ProcessBatchAsync(
        ICatalogSource source,
        RequestThrottle throttle,
        IReadOnlyList<SourceListingRef> batch,
        ImportRunOptions options,
        CatalogImportRun run,
        CancellationToken cancellationToken)
    {
        var fetched = new FetchOutcome[batch.Count];

        await Parallel.ForAsync(
            0,
            batch.Count,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, source.Throttle.MaxConcurrency),
                CancellationToken = cancellationToken
            },
            async (index, token) =>
                fetched[index] = await FetchOneAsync(source, throttle, batch[index], token)
                    .ConfigureAwait(false)).ConfigureAwait(false);

        // Counted here rather than inside the parallel body: the counters live on
        // a plain model, and making them thread-safe would be a worse trade than
        // returning the outcome and tallying once.
        var failures = fetched.Count(outcome => outcome.Failed);

        run.ItemsFailed += failures;

        var listings = new Dictionary<string, CatalogListing>(StringComparer.Ordinal);
        var observations = new Dictionary<string, List<SourceListing>>(StringComparer.Ordinal);
        var records = new List<ListingSourceRecord>();

        for (var index = 0; index < batch.Count; index++)
        {
            if (fetched[index].Listing is not { } listing)
            {
                continue;
            }

            await PlaceAsync(
                source, batch[index], listing, options, run, listings, observations, records, cancellationToken)
                .ConfigureAwait(false);
        }

        if (listings.Count > 0)
        {
            await _repository.UpsertManyAsync([.. listings.Values], cancellationToken).ConfigureAwait(false);
        }

        // After the listings, never before: an observation's row points at a
        // listing, and the foreign key is what stops a source row from outliving
        // the listing it describes.
        foreach (var record in records)
        {
            await _repository.UpsertSourceAsync(record, cancellationToken).ConfigureAwait(false);
        }

        return failures;
    }

    /// <summary>
    /// Fetches and normalises one item, deciding first whether it needs fetching
    /// at all.
    /// </summary>
    /// <param name="source">The source being imported.</param>
    /// <param name="throttle">Limits how hard the source is hit.</param>
    /// <param name="reference">The item to fetch.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The normalised observation, and whether the attempt failed.</returns>
    private async Task<FetchOutcome> FetchOneAsync(
        ICatalogSource source,
        RequestThrottle throttle,
        SourceListingRef reference,
        CancellationToken cancellationToken)
    {
        var stored = await _repository
            .GetSourceAsync(source.Key, reference.SourceItemId, cancellationToken)
            .ConfigureAwait(false);

        // The cheapest possible outcome: the source told us when it last changed
        // the item, and it has not changed since we looked. No request is made.
        if (stored is not null &&
            reference.SourceUpdatedAt is { } updated &&
            stored.SourceUpdatedAt is { } known &&
            updated <= known)
        {
            return default;
        }

        try
        {
            var fetched = await throttle
                .ExecuteAsync(token => source.FetchAsync(reference, token), cancellationToken)
                .ConfigureAwait(false);

            if (fetched is null || string.IsNullOrWhiteSpace(fetched.Title))
            {
                // A failure, not a skip. Enough of these means the source has
                // changed shape underneath a working parser, which is exactly what
                // the health check exists to notice.
                return new FetchOutcome(null, Failed: true);
            }

            return new FetchOutcome(_normalizer.Normalize(fetched), Failed: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Fetching {Item} from {Source} failed.", reference.SourceItemId, source.DisplayName);

            return new FetchOutcome(null, Failed: true);
        }
    }

    /// <summary>
    /// What one fetch attempt produced.
    /// </summary>
    /// <param name="Listing">The observation, or <see langword="null"/>.</param>
    /// <param name="Failed">
    /// Whether the attempt failed, as opposed to being skipped because nothing
    /// had changed. Only failures count against the health check.
    /// </param>
    private readonly record struct FetchOutcome(SourceListing? Listing, bool Failed);

    /// <summary>
    /// Places one observation: matches it, re-merges its listing, and stages both
    /// rows for writing.
    /// </summary>
    /// <param name="source">The source being imported.</param>
    /// <param name="reference">The reference the observation came from.</param>
    /// <param name="listing">The normalised observation.</param>
    /// <param name="options">What to import.</param>
    /// <param name="run">Counters for the pass.</param>
    /// <param name="staged">Listings staged for writing, by identity.</param>
    /// <param name="observations">Observations staged in this batch, by listing.</param>
    /// <param name="records">Source rows staged for writing.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    private async Task PlaceAsync(
        ICatalogSource source,
        SourceListingRef reference,
        SourceListing listing,
        ImportRunOptions options,
        CatalogImportRun run,
        Dictionary<string, CatalogListing> staged,
        Dictionary<string, List<SourceListing>> observations,
        List<ListingSourceRecord> records,
        CancellationToken cancellationToken)
    {
        var stored = await _repository
            .GetSourceAsync(source.Key, reference.SourceItemId, cancellationToken)
            .ConfigureAwait(false);

        var normalizedJson = JsonSerializer.Serialize(listing, JsonOptions);

        // Hashed without the raw payload. The payload is provenance, not
        // content, and it routinely carries per-response noise — the Internet
        // Archive stamps every metadata response with the time it was generated.
        // Including it made a re-fetch of an unchanged item look like a change,
        // which defeated the whole incremental path.
        var contentHash = ComputeHash(
            JsonSerializer.Serialize(listing with { RawPayload = string.Empty }, JsonOptions));

        // The second short circuit: the source did change the item, but nothing
        // we care about is different. Common when a site touches a timestamp.
        if (stored is not null &&
            string.Equals(stored.SourceContentHash, contentHash, StringComparison.Ordinal))
        {
            return;
        }

        var listingId = stored?.ListingId
                        ?? await ResolveListingIdAsync(listing, staged, run, cancellationToken)
                            .ConfigureAwait(false);

        if (listingId is null)
        {
            // Ambiguous: the title matches something already in the catalogue but
            // the years are too far apart to be the same release. Recorded and
            // left alone rather than guessed at.
            run.ItemsFailed++;
            return;
        }

        if (!observations.TryGetValue(listingId, out var forListing))
        {
            forListing = [.. (await _repository.GetSourceListingsAsync(listingId, cancellationToken)
                .ConfigureAwait(false))];

            observations[listingId] = forListing;
        }

        // Replace this source's previous view rather than adding a second one.
        forListing.RemoveAll(existing =>
            string.Equals(existing.SourceKey, listing.SourceKey, StringComparison.OrdinalIgnoreCase));

        forListing.Add(listing);

        var merged = _merger.Merge(listingId, forListing, options.CaptureMergeTrace);

        merged.Listing.FieldProvenance = JsonSerializer.Serialize(merged.FieldProvenance, JsonOptions);

        if (merged.Trace is { Count: > 0 })
        {
            foreach (var entry in merged.Trace)
            {
                _logger.LogDebug(
                    "Merge {Listing}.{Field}: {Source} offered {Value} ({Rule}){Won}",
                    listingId, entry.Field, entry.SourceKey, entry.Value, entry.Rule,
                    entry.Won ? " — won" : string.Empty);
            }
        }

        staged[listingId] = merged.Listing;
        run.ItemsChanged++;

        records.Add(new ListingSourceRecord
        {
            ListingId = listingId,
            SourceKey = listing.SourceKey,
            SourceItemId = listing.SourceItemId,
            SourceUrl = listing.SourceUrl.AbsoluteUri,
            NormalizedJson = normalizedJson,
            RawPayload = Compress(listing.RawPayload),
            SourceUpdatedAt = listing.SourceUpdatedAt ?? reference.SourceUpdatedAt,
            FetchedAt = DateTimeOffset.Now,
            SourceContentHash = contentHash,
            Rank = source.Rank
        });
    }

    /// <summary>
    /// Decides which listing an observation belongs to, minting one if it is new.
    /// </summary>
    /// <param name="listing">The observation being placed.</param>
    /// <param name="staged">Listings already staged in this batch.</param>
    /// <param name="run">Counters for the pass.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The listing identity, or <see langword="null"/> when the match is ambiguous.</returns>
    private async Task<string?> ResolveListingIdAsync(
        SourceListing listing,
        IReadOnlyDictionary<string, CatalogListing> staged,
        CatalogImportRun run,
        CancellationToken cancellationToken)
    {
        var matchKey = _normalizer.ComputeMatchKey(listing.Title, listing.Year);

        // An operator's explicit "these are the same game" decision outranks
        // anything the matcher would work out for itself.
        var alias = await _repository.ResolveAliasAsync(matchKey, cancellationToken).ConfigureAwait(false);

        if (alias is not null)
        {
            return alias;
        }

        var titleKey = _normalizer.ComputeTitleKey(listing.Title);

        var candidates = new List<CatalogListing>(
            await _repository.FindByTitleKeyAsync(titleKey, cancellationToken).ConfigureAwait(false));

        // Listings created earlier in this batch are not in the database yet.
        // Without them, two observations of the same new game arriving together
        // would each mint an identity.
        foreach (var pending in staged.Values)
        {
            if (candidates.All(candidate =>
                    !string.Equals(candidate.ListingId, pending.ListingId, StringComparison.Ordinal)))
            {
                candidates.Add(pending);
            }
        }

        var match = _matcher.Match(listing, candidates);

        switch (match.Kind)
        {
            case ListingMatchKind.Exact:
            case ListingMatchKind.Fuzzy:
                return match.ListingId;

            case ListingMatchKind.Ambiguous:
                _logger.LogInformation(
                    "'{Title}' ({Year}) was not merged: {Reason}.", listing.Title, listing.Year, match.Reason);

                return null;

            default:
                run.ListingsAdded++;
                return CatalogListing.IdPrefix + Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>
    /// Re-runs normalisation and merging over stored payloads, touching no source.
    /// </summary>
    /// <param name="options">What to re-merge.</param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass produced.</returns>
    /// <remarks>
    /// The mode used whenever a normalisation or merge rule changes. It applies
    /// the new rules to the whole catalogue in seconds, offline — which is only
    /// possible because every observation was stored when it was first fetched.
    /// </remarks>
    private async Task<ImportSourceResult> RemergeAsync(
        ImportRunOptions options,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceKey = options.SourceKeys is { Count: 1 } only ? only[0] : null;

        var ids = await _repository.GetListingIdsWithSourcesAsync(sourceKey, cancellationToken)
            .ConfigureAwait(false);

        var runId = await _repository.StartRunAsync("remerge", ImportMode.Remerge, cancellationToken)
            .ConfigureAwait(false);

        var run = new CatalogImportRun { RunId = runId, SourceKey = "remerge", Mode = ImportMode.Remerge };
        var staged = new List<CatalogListing>(BatchSize);

        foreach (var listingId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            run.ItemsSeen++;

            var observations = await _repository.GetSourceListingsAsync(listingId, cancellationToken)
                .ConfigureAwait(false);

            if (observations.Count == 0)
            {
                continue;
            }

            // Re-normalised, not just re-merged: a change to the normalisation
            // rules is the most common reason to run this at all.
            var renormalized = observations.Select(_normalizer.Normalize).ToArray();
            var merged = _merger.Merge(listingId, renormalized, options.CaptureMergeTrace);

            merged.Listing.FieldProvenance = JsonSerializer.Serialize(merged.FieldProvenance, JsonOptions);

            staged.Add(merged.Listing);

            if (staged.Count < BatchSize)
            {
                continue;
            }

            run.ItemsChanged += await _repository.UpsertManyAsync(staged, cancellationToken)
                .ConfigureAwait(false);

            staged.Clear();

            progress?.Report(new ImportProgress(
                "remerge", run.ItemsSeen, run.ItemsChanged, $"Re-merged {run.ItemsSeen} of {ids.Count}"));
        }

        if (staged.Count > 0)
        {
            run.ItemsChanged += await _repository.UpsertManyAsync(staged, cancellationToken)
                .ConfigureAwait(false);
        }

        await _repository.CompleteRunAsync(run, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Re-merged {Seen} listings; {Changed} changed.", run.ItemsSeen, run.ItemsChanged);

        return new ImportSourceResult(
            "remerge", "Re-merge", run.ItemsSeen, run.ItemsChanged, 0, 0, false, null);
    }

    /// <summary>Determines whether a pass has failed often enough to stop.</summary>
    /// <param name="attempted">How many items have been attempted.</param>
    /// <param name="failures">How many of them failed to parse.</param>
    /// <returns><see langword="true"/> when the pass should abort.</returns>
    private static bool IsUnhealthy(int attempted, int failures) =>
        attempted >= HealthCheckSampleSize &&
        (double)(attempted - failures) / attempted < MinimumParseSuccessRate;

    /// <summary>Explains why a pass was stopped.</summary>
    /// <param name="source">The source that stopped parsing.</param>
    /// <param name="attempted">How many items were attempted.</param>
    /// <param name="failures">How many of them failed.</param>
    /// <returns>A message for the log and the run record.</returns>
    private static string DescribeCollapse(ICatalogSource source, int attempted, int failures) =>
        $"Stopped after {attempted} items: only {(attempted - failures) * 100 / attempted}% parsed. " +
        $"{source.DisplayName} has probably changed shape.";

    /// <summary>Hashes a normalised observation.</summary>
    /// <param name="value">The serialised observation.</param>
    /// <returns>A lowercase hexadecimal digest.</returns>
    private static string ComputeHash(string value) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Compresses a raw payload for storage.
    /// </summary>
    /// <param name="payload">The payload as the source returned it.</param>
    /// <returns>The compressed bytes, or <see langword="null"/> when there was nothing to store.</returns>
    /// <remarks>
    /// Payloads are HTML and JSON, which compress to a fraction of their size.
    /// Several thousand of them uncompressed would be the largest thing in the
    /// database by an order of magnitude.
    /// </remarks>
    private static byte[]? Compress(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompresses a stored raw payload.
    /// </summary>
    /// <param name="payload">The stored bytes.</param>
    /// <returns>The original text, or <see langword="null"/> when there was none.</returns>
    public static string? Decompress(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
        {
            return null;
        }

        using var input = new MemoryStream(payload);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
