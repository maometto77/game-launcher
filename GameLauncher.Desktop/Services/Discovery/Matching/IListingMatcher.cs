using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Discovery.Matching;

/// <summary>
/// How confidently a source observation was tied to an existing listing.
/// </summary>
public enum ListingMatchKind
{
    /// <summary>Nothing matched; this is a game the catalogue has not seen.</summary>
    None = 0,

    /// <summary>Title and year both agree.</summary>
    Exact = 1,

    /// <summary>
    /// Titles agree and the years are close enough to be the same release.
    /// </summary>
    Fuzzy = 2,

    /// <summary>
    /// Titles agree but the years are too far apart, or several listings match
    /// equally well.
    /// </summary>
    /// <remarks>
    /// Deliberately not a merge. Two games sharing a title several years apart
    /// are far more often a remake or a sequel than one release described twice,
    /// and merging them destroys a distinction that cannot be recovered from the
    /// merged row.
    /// </remarks>
    Ambiguous = 3
}

/// <summary>
/// The outcome of matching one observation against the catalogue.
/// </summary>
/// <param name="Kind">How confident the match is.</param>
/// <param name="ListingId">
/// The listing matched, or <see langword="null"/> for
/// <see cref="ListingMatchKind.None"/> and <see cref="ListingMatchKind.Ambiguous"/>.
/// </param>
/// <param name="Reason">A short explanation, for logs and for diagnosing rules.</param>
public sealed record ListingMatch(ListingMatchKind Kind, string? ListingId, string? Reason);

/// <summary>
/// Decides whether a source observation describes a game the catalogue already
/// holds.
/// </summary>
/// <remarks>
/// Pure: it is given the candidate and the listings worth comparing against, and
/// returns a verdict. Finding those candidates is the repository's job, and
/// acting on the verdict is the pipeline's.
/// </remarks>
public interface IListingMatcher
{
    /// <summary>
    /// Matches an observation against candidate listings.
    /// </summary>
    /// <param name="candidate">The observation being placed.</param>
    /// <param name="nearby">
    /// Listings sharing the candidate's normalised title, from
    /// <c>ICatalogListingRepository.FindByTitleKeyAsync</c>.
    /// </param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    ListingMatch Match(SourceListing candidate, IReadOnlyList<CatalogListing> nearby);
}
