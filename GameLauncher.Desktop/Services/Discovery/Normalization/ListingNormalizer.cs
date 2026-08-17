using System.Net;
using System.Text.RegularExpressions;

namespace GameLauncher.Desktop.Services.Discovery.Normalization;

/// <summary>
/// Default <see cref="IListingNormalizer"/>.
/// </summary>
public sealed partial class ListingNormalizer : IListingNormalizer
{
    /// <summary>
    /// Longest description kept. Some sources paste an entire manual into the
    /// field, which is not a description and makes the details page unusable.
    /// </summary>
    private const int MaxDescriptionLength = 4000;

    /// <summary>Earliest year accepted, a little before the first commercial games.</summary>
    private const int MinimumYear = 1950;

    /// <inheritdoc />
    public SourceListing Normalize(SourceListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return listing with
        {
            Title = TitleNormalizer.RestoreLeadingArticle(listing.Title),
            Year = NormalizeYear(listing.Year),
            Description = NormalizeDescription(listing.Description),
            Developer = CompanyNormalizer.Clean(listing.Developer),
            Publisher = CompanyNormalizer.Clean(listing.Publisher),
            Genres = GenreVocabulary.MapMany(listing.Genres),
            Platforms = DistinctTrimmed(listing.Platforms),
            Tags = DistinctTrimmed(listing.Tags),
            SystemRequirements = NormalizeDescription(listing.SystemRequirements),
            Images = DeduplicateImages(listing.Images),
            Downloads = DeduplicateDownloads(listing.Downloads)
        };
    }

    /// <inheritdoc />
    public string ComputeMatchKey(string title, int? year) =>
        TitleNormalizer.ComputeMatchKey(title, NormalizeYear(year));

    /// <inheritdoc />
    public string ComputeTitleKey(string title) => TitleNormalizer.ComputeTitleKey(title);

    /// <summary>
    /// Rejects years that cannot be a release date.
    /// </summary>
    /// <param name="year">The candidate year.</param>
    /// <returns>The year, or <see langword="null"/> when it is not plausible.</returns>
    /// <remarks>
    /// Sources put upload dates, scan dates and parse failures in this field. A
    /// wrong year is worse than a missing one because it participates in the
    /// match key, so an implausible value is discarded rather than carried.
    /// </remarks>
    private static int? NormalizeYear(int? year)
    {
        if (year is null)
        {
            return null;
        }

        // Guarded against a clock skewed into the future as well as bad data.
        var upperBound = DateTimeOffset.UtcNow.Year + 1;

        return year.Value >= MinimumYear && year.Value <= upperBound ? year : null;
    }

    /// <summary>
    /// Turns a possibly-HTML description into plain text.
    /// </summary>
    /// <param name="value">The raw description.</param>
    /// <returns>Plain text, truncated if very long, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Internet Archive descriptions are HTML fragments. Stripping is done here
    /// with a tag pattern rather than a parser because the input is a fragment
    /// with no document structure to respect, and the output is plain text — a
    /// full HTML parse would cost more and answer the same question.
    /// </remarks>
    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Block-level tags become breaks before everything else is dropped, or
        // paragraphs run together into one wall of text.
        var text = BlockTagPattern().Replace(value, "\n");
        text = TagPattern().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = HorizontalWhitespacePattern().Replace(text, " ");
        text = ExcessNewlinePattern().Replace(text, "\n\n").Trim();

        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= MaxDescriptionLength
            ? text
            : text[..MaxDescriptionLength].TrimEnd() + "…";
    }

    /// <summary>Trims, drops blanks and removes case-insensitive duplicates.</summary>
    /// <param name="values">The values to tidy.</param>
    /// <returns>The tidied values, in the order first seen.</returns>
    private static IReadOnlyList<string> DistinctTrimmed(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var seen = new List<string>(values.Count);

        foreach (var value in values)
        {
            var trimmed = value?.Trim();

            if (!string.IsNullOrEmpty(trimmed) &&
                !seen.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(trimmed);
            }
        }

        return seen;
    }

    /// <summary>Removes images that point at the same address.</summary>
    /// <param name="images">The images to deduplicate.</param>
    /// <returns>One entry per address, in the order first seen.</returns>
    private static IReadOnlyList<ListingImageRef> DeduplicateImages(IReadOnlyList<ListingImageRef>? images)
    {
        if (images is null || images.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return images
            .Where(image => image is not null && seen.Add(image.Url.AbsoluteUri))
            .ToArray();
    }

    /// <summary>Removes downloads that point at the same address.</summary>
    /// <param name="downloads">The downloads to deduplicate.</param>
    /// <returns>One entry per address, in the order first seen.</returns>
    private static IReadOnlyList<ListingDownloadRef> DeduplicateDownloads(
        IReadOnlyList<ListingDownloadRef>? downloads)
    {
        if (downloads is null || downloads.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return downloads
            .Where(download => download is not null && seen.Add(download.Url.AbsoluteUri))
            .ToArray();
    }

    /// <summary>Matches block-level tags that should become line breaks.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(
        @"</?(?:p|div|br|li|tr|h[1-6])\b[^>]*>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagPattern();

    /// <summary>Matches any remaining tag.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>Matches runs of spaces and tabs, but not newlines.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"[^\S\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespacePattern();

    /// <summary>Matches three or more consecutive newlines.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessNewlinePattern();
}
