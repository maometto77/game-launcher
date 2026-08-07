using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Normalization;

namespace GameLauncher.Desktop.Services.Discovery.Matching;

/// <summary>
/// Default <see cref="IListingMatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two passes. The first looks for an exact agreement on title and year. The
/// second accepts titles that agree when the years are within
/// <see cref="YearTolerance"/> of each other, or when only one side has a year at
/// all.
/// </para>
/// <para>
/// The tolerance exists because sources routinely disagree by a year: one
/// records the original release, another a regional release, a re-release or the
/// date a disk image was made. Widening it further starts absorbing sequels, and
/// there is no rule that recovers from that.
/// </para>
/// </remarks>
public sealed class ListingMatcher : IListingMatcher
{
    /// <summary>How far apart two years may be and still be the same release.</summary>
    private const int YearTolerance = 1;

    private readonly IListingNormalizer _normalizer;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="normalizer">Computes the keys compared here.</param>
    /// <exception cref="ArgumentNullException"><paramref name="normalizer"/> is <see langword="null"/>.</exception>
    public ListingMatcher(IListingNormalizer normalizer) =>
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));

    /// <inheritdoc />
    public ListingMatch Match(SourceListing candidate, IReadOnlyList<CatalogListing> nearby)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(nearby);

        if (nearby.Count == 0)
        {
            return new ListingMatch(ListingMatchKind.None, null, "no listing shares this title");
        }

        var titleKey = _normalizer.ComputeTitleKey(candidate.Title);

        if (titleKey.Length == 0)
        {
            // A title that normalises to nothing cannot be matched on, and
            // matching on year alone would merge every untitled item together.
            return new ListingMatch(ListingMatchKind.None, null, "title normalises to nothing");
        }

        var matchKey = _normalizer.ComputeMatchKey(candidate.Title, candidate.Year);

        var exact = nearby
            .Where(listing => string.Equals(listing.MatchKey, matchKey, StringComparison.Ordinal))
            .ToArray();

        if (exact.Length == 1)
        {
            return new ListingMatch(ListingMatchKind.Exact, exact[0].ListingId, "title and year agree");
        }

        if (exact.Length > 1)
        {
            // The catalogue already holds a duplicate. Adding a third row would
            // compound it, and picking one arbitrarily would attach this
            // observation to whichever happened to sort first.
            return new ListingMatch(
                ListingMatchKind.Ambiguous, null, $"{exact.Length} listings already share this key");
        }

        var compatible = nearby
            .Where(listing => IsSameTitle(listing, titleKey) && IsCompatibleYear(listing.Year, candidate.Year))
            .ToArray();

        return compatible.Length switch
        {
            1 => new ListingMatch(
                ListingMatchKind.Fuzzy,
                compatible[0].ListingId,
                DescribeYearMatch(compatible[0].Year, candidate.Year)),

            0 when nearby.Any(listing => IsSameTitle(listing, titleKey)) => new ListingMatch(
                ListingMatchKind.Ambiguous,
                null,
                "title agrees but the years are too far apart to be the same release"),

            0 => new ListingMatch(ListingMatchKind.None, null, "no listing shares this title"),

            _ => new ListingMatch(
                ListingMatchKind.Ambiguous, null, $"{compatible.Length} listings match equally well")
        };
    }

    /// <summary>Determines whether a listing's title normalises to the candidate's.</summary>
    /// <param name="listing">The listing to test.</param>
    /// <param name="titleKey">The candidate's normalised title.</param>
    /// <returns><see langword="true"/> when the titles agree.</returns>
    /// <remarks>
    /// Compares against the listing's stored <see cref="CatalogListing.MatchKey"/>
    /// prefix rather than re-normalising its title, so a change to the
    /// normalisation rules cannot make stored rows silently unmatchable until
    /// they are rebuilt.
    /// </remarks>
    private static bool IsSameTitle(CatalogListing listing, string titleKey)
    {
        var separator = listing.MatchKey.LastIndexOf('|');
        var storedTitle = separator < 0 ? listing.MatchKey : listing.MatchKey[..separator];

        return string.Equals(storedTitle, titleKey, StringComparison.Ordinal);
    }

    /// <summary>Determines whether two years can describe the same release.</summary>
    /// <param name="stored">The listing's year.</param>
    /// <param name="candidate">The observation's year.</param>
    /// <returns><see langword="true"/> when they are compatible.</returns>
    private static bool IsCompatibleYear(int? stored, int? candidate)
    {
        // One side not knowing the year is not disagreement. The merge adopts
        // whichever value exists.
        if (stored is null || candidate is null)
        {
            return true;
        }

        return Math.Abs(stored.Value - candidate.Value) <= YearTolerance;
    }

    /// <summary>Explains why two years were accepted.</summary>
    /// <param name="stored">The listing's year.</param>
    /// <param name="candidate">The observation's year.</param>
    /// <returns>A short explanation.</returns>
    private static string DescribeYearMatch(int? stored, int? candidate) =>
        (stored, candidate) switch
        {
            (null, not null) => "title agrees; listing had no year",
            (not null, null) => "title agrees; observation had no year",
            (null, null) => "title agrees; neither side has a year",
            _ when stored == candidate => "title and year agree",
            _ => $"title agrees; years differ by {Math.Abs(stored!.Value - candidate!.Value)}"
        };
}
