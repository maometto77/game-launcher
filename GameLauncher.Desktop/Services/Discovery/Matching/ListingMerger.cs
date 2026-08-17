using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Normalization;

namespace GameLauncher.Desktop.Services.Discovery.Matching;

/// <summary>
/// Default <see cref="IListingMerger"/>.
/// </summary>
public sealed class ListingMerger : IListingMerger
{
    /// <summary>Rank given to a source that is no longer registered.</summary>
    /// <remarks>
    /// Its rows are kept and still contribute, but they lose every tie to a
    /// source that is still installed. Discarding them instead would silently
    /// delete metadata the moment a source was disabled.
    /// </remarks>
    private const int UnknownSourceRank = int.MaxValue;

    private readonly IListingNormalizer _normalizer;
    private readonly IReadOnlyDictionary<string, int> _ranks;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="normalizer">Computes the match key for the merged row.</param>
    /// <param name="sources">Registered sources, read only for their ranks.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ListingMerger(IListingNormalizer normalizer, IEnumerable<ICatalogSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));

        // Last registration wins rather than throwing: the engine that owns the
        // duplicate-key guard is the import service, and duplicating the check
        // here would fail construction of an unrelated component.
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            ranks[source.Key] = source.Rank;
        }

        _ranks = ranks;
    }

    /// <inheritdoc />
    public MergeResult Merge(
        string listingId,
        IReadOnlyList<SourceListing> sources,
        bool captureTrace = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one source observation is required.", nameof(sources));
        }

        // Ordering once here is what makes every "first non-empty" rule below a
        // precedence rule rather than an accident of enumeration order.
        var ordered = sources
            .OrderBy(source => _ranks.TryGetValue(source.SourceKey, out var rank) ? rank : UnknownSourceRank)
            .ThenBy(source => source.SourceKey, StringComparer.Ordinal)
            .ToArray();

        var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
        var trace = captureTrace ? new List<MergeTraceEntry>() : null;

        var title = PickByRank(ordered, nameof(CatalogListing.Title), s => s.Title, provenance, trace)
                    ?? ordered[0].Title;

        var year = PickYear(ordered, provenance, trace);
        var description = PickLongest(ordered, nameof(CatalogListing.Description), s => s.Description, provenance, trace);
        var developer = PickByRank(ordered, nameof(CatalogListing.Developer), s => s.Developer, provenance, trace);
        var publisher = PickByRank(ordered, nameof(CatalogListing.Publisher), s => s.Publisher, provenance, trace);

        var requirements = PickLongest(
            ordered, nameof(CatalogListing.SystemRequirements), s => s.SystemRequirements, provenance, trace);

        var genres = Union(ordered, source => source.Genres);
        var platforms = Union(ordered, source => source.Platforms);
        var tags = Union(ordered, source => source.Tags);

        var images = MergeImages(ordered);
        var downloads = MergeDownloads(ordered);

        // Any source offering a file makes the game installable. One source
        // restricting access says nothing about another's copy.
        var downloadable = downloads.Count > 0 && ordered.Any(source => source.IsDownloadable);

        var listing = new CatalogListing
        {
            ListingId = listingId,
            Title = title,
            SortTitle = TitleNormalizer.ToSortTitle(title),
            Year = year,
            Developer = developer,
            Publisher = publisher,
            Description = description,
            SystemRequirements = requirements,
            MatchKey = _normalizer.ComputeMatchKey(title, year),
            CoverImageUrl = images
                .FirstOrDefault(image => image.Kind == ListingImageKind.Cover)?.RemoteUrl,
            PrimarySourceKey = DeterminePrimarySource(ordered, provenance),
            IsDownloadable = downloadable,
            Genres = genres,
            Platforms = platforms,
            Tags = tags,
            Images = images,
            Downloads = downloads
        };

        listing.ContentHash = ComputeContentHash(listing);

        return new MergeResult(listing, provenance, trace);
    }

    /// <summary>
    /// Takes the first non-empty value in precedence order.
    /// </summary>
    /// <param name="ordered">Observations, already in precedence order.</param>
    /// <param name="field">Field being resolved, for provenance.</param>
    /// <param name="selector">Reads the field from an observation.</param>
    /// <param name="provenance">Receives the winning source.</param>
    /// <param name="trace">Receives every candidate, when capturing.</param>
    /// <returns>The winning value, or <see langword="null"/> when no source had one.</returns>
    private static string? PickByRank(
        IReadOnlyList<SourceListing> ordered,
        string field,
        Func<SourceListing, string?> selector,
        IDictionary<string, string> provenance,
        List<MergeTraceEntry>? trace)
    {
        string? winner = null;
        var winningIndex = -1;

        for (var index = 0; index < ordered.Count; index++)
        {
            var value = selector(ordered[index]);

            // The first usable value wins. The scan continues only so the trace
            // records what the other sources would have offered.
            if (winningIndex < 0 && !string.IsNullOrWhiteSpace(value))
            {
                winner = value;
                winningIndex = index;
            }
        }

        if (trace is not null)
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                trace.Add(new MergeTraceEntry(
                    field,
                    ordered[index].SourceKey,
                    Summarize(selector(ordered[index])),
                    index == winningIndex,
                    "first by rank"));
            }
        }

        if (winningIndex >= 0)
        {
            provenance[field] = ordered[winningIndex].SourceKey;
        }

        return winner;
    }

    /// <summary>
    /// Takes the longest non-empty value.
    /// </summary>
    /// <param name="ordered">Observations, already in precedence order.</param>
    /// <param name="field">Field being resolved, for provenance.</param>
    /// <param name="selector">Reads the field from an observation.</param>
    /// <param name="provenance">Receives the winning source.</param>
    /// <param name="trace">Receives every candidate, when capturing.</param>
    /// <returns>The winning value, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Used for prose. Neither source is reliably richer, and a longer
    /// description is nearly always the more complete one — whereas taking it by
    /// rank would discard a full description in favour of a one-line stub.
    /// </remarks>
    private static string? PickLongest(
        IReadOnlyList<SourceListing> ordered,
        string field,
        Func<SourceListing, string?> selector,
        IDictionary<string, string> provenance,
        List<MergeTraceEntry>? trace)
    {
        string? winner = null;
        string? winningSource = null;

        foreach (var source in ordered)
        {
            var value = selector(source);

            if (!string.IsNullOrWhiteSpace(value) && value.Length > (winner?.Length ?? 0))
            {
                winner = value;
                winningSource = source.SourceKey;
            }
        }

        if (trace is not null)
        {
            foreach (var source in ordered)
            {
                var value = selector(source);

                trace.Add(new MergeTraceEntry(
                    field,
                    source.SourceKey,
                    Summarize(value),
                    string.Equals(source.SourceKey, winningSource, StringComparison.Ordinal),
                    "longest"));
            }
        }

        if (winningSource is not null)
        {
            provenance[field] = winningSource;
        }

        return winner;
    }

    /// <summary>
    /// Takes the earliest year any source reports.
    /// </summary>
    /// <param name="ordered">Observations, already in precedence order.</param>
    /// <param name="provenance">Receives the winning source.</param>
    /// <param name="trace">Receives every candidate, when capturing.</param>
    /// <returns>The earliest year, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Re-release, regional and compilation dates all move later than the
    /// original; almost nothing moves earlier. Taking the earliest therefore
    /// converges on the original release, whereas taking it by source rank would
    /// give whichever source happened to describe a re-release.
    /// </remarks>
    private static int? PickYear(
        IReadOnlyList<SourceListing> ordered,
        IDictionary<string, string> provenance,
        List<MergeTraceEntry>? trace)
    {
        int? winner = null;
        string? winningSource = null;

        foreach (var source in ordered)
        {
            if (source.Year is { } year && (winner is null || year < winner))
            {
                winner = year;
                winningSource = source.SourceKey;
            }
        }

        if (trace is not null)
        {
            foreach (var source in ordered)
            {
                trace.Add(new MergeTraceEntry(
                    nameof(CatalogListing.Year),
                    source.SourceKey,
                    source.Year?.ToString(CultureInfo.InvariantCulture),
                    string.Equals(source.SourceKey, winningSource, StringComparison.Ordinal),
                    "earliest"));
            }
        }

        if (winningSource is not null)
        {
            provenance[nameof(CatalogListing.Year)] = winningSource;
        }

        return winner;
    }

    /// <summary>
    /// Unions a collection field across sources, preserving precedence order.
    /// </summary>
    /// <param name="ordered">Observations, already in precedence order.</param>
    /// <param name="selector">Reads the collection from an observation.</param>
    /// <returns>Every distinct value, in the order first seen.</returns>
    private static IReadOnlyList<string> Union(
        IReadOnlyList<SourceListing> ordered,
        Func<SourceListing, IReadOnlyList<string>> selector)
    {
        var seen = new List<string>();

        foreach (var value in ordered.SelectMany(selector))
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !seen.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(value);
            }
        }

        return seen;
    }

    /// <summary>
    /// Unions images across sources, deduplicating by address.
    /// </summary>
    /// <param name="ordered">Observations, already in precedence order.</param>
    /// <returns>Images, covers first, then by source precedence and stated order.</returns>
    private static IReadOnlyList<ListingImage> MergeImages(IReadOnlyList<SourceListing> ordered)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<ListingImage>();

        foreach (var source in ordered)
        {
            foreach (var image in source.Images.OrderBy(image => image.SortOrder))
            {
                if (!seen.Add(image.Url.AbsoluteUri))
                {
                    continue;
                }

                merged.Add(new ListingImage
                {
                    SourceKey = source.SourceKey,
                    Kind = image.Kind,
                    RemoteUrl = image.Url.AbsoluteUri,
                    Width = image.Width,
                    Height = image.Height,
                    SortOrder = merged.Count
                });
            }
        }

        // Covers first so the tile has something to show without scanning.
        return merged
            .OrderBy(image => image.Kind == ListingImageKind.Cover ? 0 : 1)
            .ThenBy(image => image.SortOrder)
            .ToArray();
    }

    /// <summary>
    /// Unions downloads across sources, deduplicating by address.
    /// </summary>
    /// <param name="ordered">Observations, already in precedence order.</param>
    /// <returns>Downloads, with mirror rank assigned by source precedence.</returns>
    /// <remarks>
    /// Mirrors are always additive. A second source offering the same game does
    /// not replace the first's links — it adds somewhere else to get the file,
    /// which is exactly what makes a failed transfer recoverable.
    /// </remarks>
    private static IReadOnlyList<ListingDownload> MergeDownloads(IReadOnlyList<SourceListing> ordered)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<ListingDownload>();

        foreach (var source in ordered)
        {
            foreach (var download in source.Downloads.OrderBy(download => download.MirrorRank))
            {
                if (!seen.Add(download.Url.AbsoluteUri))
                {
                    continue;
                }

                merged.Add(new ListingDownload
                {
                    SourceKey = source.SourceKey,
                    Url = download.Url.AbsoluteUri,
                    FileName = download.FileName,
                    SizeBytes = download.SizeBytes,
                    Md5 = download.Md5,
                    Sha1 = download.Sha1,
                    Format = download.Format,
                    Kind = download.Kind,
                    MirrorRank = merged.Count
                });
            }
        }

        return merged;
    }

    /// <summary>
    /// Names the source that contributed the most fields.
    /// </summary>
    /// <param name="ordered">Observations, already in precedence order.</param>
    /// <param name="provenance">Which source won each field.</param>
    /// <returns>The dominant source's key.</returns>
    private static string DeterminePrimarySource(
        IReadOnlyList<SourceListing> ordered,
        IReadOnlyDictionary<string, string> provenance)
    {
        if (provenance.Count == 0)
        {
            return ordered[0].SourceKey;
        }

        return provenance.Values
            .GroupBy(key => key, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())

            // Ties break towards the higher-ranked source, which is first in the
            // ordered list, so the result is stable rather than dictionary order.
            .ThenBy(group => ordered
                .Select((source, index) => (source, index))
                .First(entry => string.Equals(entry.source.SourceKey, group.Key, StringComparison.Ordinal))
                .index)
            .First()
            .Key;
    }

    /// <summary>
    /// Hashes the merged content so an unchanged merge can skip its write.
    /// </summary>
    /// <param name="listing">The merged row.</param>
    /// <returns>A lowercase hexadecimal digest.</returns>
    /// <remarks>
    /// Covers only fields a source can influence. The identity, timestamps and
    /// cached image paths are excluded, or every pass would look like a change.
    /// </remarks>
    private static string ComputeContentHash(CatalogListing listing)
    {
        // A control character no metadata value can contain, so two different
        // field layouts cannot run together and hash the same.
        const char Separator = '\u001F';

        var builder = new StringBuilder();

        builder.Append(listing.Title).Append(Separator)
            .Append(listing.Year?.ToString(CultureInfo.InvariantCulture)).Append(Separator)
            .Append(listing.Developer).Append(Separator)
            .Append(listing.Publisher).Append(Separator)
            .Append(listing.Description).Append(Separator)
            .Append(listing.SystemRequirements).Append(Separator)
            .Append(listing.IsDownloadable).Append(Separator)
            .AppendJoin(',', listing.Genres).Append(Separator)
            .AppendJoin(',', listing.Platforms).Append(Separator)
            .AppendJoin(',', listing.Tags).Append(Separator)
            .AppendJoin(',', listing.Images.Select(image => image.RemoteUrl)).Append(Separator)
            .AppendJoin(',', listing.Downloads.Select(download => download.Url));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>Shortens a long value so a trace entry stays readable.</summary>
    /// <param name="value">The value to shorten.</param>
    /// <returns>The value, truncated if long.</returns>
    private static string? Summarize(string? value) =>
        value is { Length: > 120 } ? value[..120] + "…" : value;
}
