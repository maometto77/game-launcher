using System.Runtime.CompilerServices;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Matching;
using GameLauncher.Desktop.Services.Discovery.Normalization;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers duplicate detection and the merge policy: scalars by per-field
/// precedence, collections unioned, and a null never overwriting a value.
/// </summary>
public sealed class MatchAndMergeTests
{
    private const string Primary = "primary";
    private const string Secondary = "secondary";

    private static readonly ListingNormalizer Normalizer = new();

    private static ListingMatcher Matcher() => new(Normalizer);

    private static ListingMerger Merger() => new(
        Normalizer,
        [new StubSource(Primary, rank: 0), new StubSource(Secondary, rank: 1)]);

    [Fact]
    public void An_empty_catalogue_never_matches() =>
        Assert.Equal(
            ListingMatchKind.None,
            Matcher().Match(Listing("Doom", 1993), []).Kind);

    [Fact]
    public void The_same_title_and_year_match_exactly()
    {
        var existing = Merged("Doom", 1993);

        var match = Matcher().Match(Listing("Doom", 1993), [existing]);

        Assert.Equal(ListingMatchKind.Exact, match.Kind);
        Assert.Equal(existing.ListingId, match.ListingId);
    }

    [Theory]
    [InlineData(1993, 1994)]
    [InlineData(1994, 1993)]
    [InlineData(1993, null)]
    [InlineData(null, 1993)]
    public void Years_within_a_year_still_match(int? stored, int? candidate)
    {
        // Sources routinely disagree by a year: original versus regional release,
        // or the date a disk image was made.
        var match = Matcher().Match(Listing("Doom", candidate), [Merged("Doom", stored)]);

        Assert.Equal(ListingMatchKind.Fuzzy, match.Kind);
    }

    [Fact]
    public void Years_further_apart_are_flagged_rather_than_merged()
    {
        // Prince of Persia 1989 and 2008 are a game and its remake. Merging them
        // destroys a distinction the merged row cannot recover.
        var match = Matcher().Match(
            Listing("Prince of Persia", 2008), [Merged("Prince of Persia", 1989)]);

        Assert.Equal(ListingMatchKind.Ambiguous, match.Kind);
        Assert.Null(match.ListingId);
    }

    [Fact]
    public void Several_equally_good_candidates_are_ambiguous_rather_than_arbitrary()
    {
        var match = Matcher().Match(
            Listing("Doom", 1993), [Merged("Doom", 1993), Merged("Doom", 1993)]);

        Assert.Equal(ListingMatchKind.Ambiguous, match.Kind);
        Assert.Null(match.ListingId);
    }

    [Fact]
    public void A_title_that_normalises_to_nothing_never_matches()
    {
        var match = Matcher().Match(Listing("!!!", 1993), [Merged("Doom", 1993)]);

        Assert.Equal(ListingMatchKind.None, match.Kind);
    }

    [Fact]
    public void Scalars_resolve_by_source_precedence()
    {
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1993) with { SourceKey = Secondary, Developer = "Wrong" },
            Listing("DOOM", 1993) with { SourceKey = Primary, Developer = "id Software" }
        ]);

        Assert.Equal("DOOM", result.Listing.Title);
        Assert.Equal("id Software", result.Listing.Developer);
        Assert.Equal(Primary, result.FieldProvenance[nameof(CatalogListing.Developer)]);
    }

    [Fact]
    public void A_null_never_overwrites_a_value()
    {
        // This is what stops a parser that has silently broken from hollowing out
        // metadata a working source already supplied.
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1993) with { SourceKey = Primary, Developer = null, Publisher = null },
            Listing("Doom", 1993) with { SourceKey = Secondary, Developer = "id Software", Publisher = "GT" }
        ]);

        Assert.Equal("id Software", result.Listing.Developer);
        Assert.Equal("GT", result.Listing.Publisher);
        Assert.Equal(Secondary, result.FieldProvenance[nameof(CatalogListing.Developer)]);
    }

    [Fact]
    public void The_earliest_year_wins_because_re_releases_only_move_later()
    {
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1995) with { SourceKey = Primary },
            Listing("Doom", 1993) with { SourceKey = Secondary }
        ]);

        Assert.Equal(1993, result.Listing.Year);
        Assert.Equal(Secondary, result.FieldProvenance[nameof(CatalogListing.Year)]);
    }

    [Fact]
    public void The_longest_description_wins_regardless_of_rank()
    {
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1993) with { SourceKey = Primary, Description = "A shooter." },
            Listing("Doom", 1993) with { SourceKey = Secondary, Description = "A far more complete account." }
        ]);

        Assert.Equal("A far more complete account.", result.Listing.Description);
    }

    [Fact]
    public void Collections_union_rather_than_replace()
    {
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1993) with { SourceKey = Primary, Genres = ["Action"], Platforms = ["DOS"] },
            Listing("Doom", 1993) with { SourceKey = Secondary, Genres = ["Shooter"], Platforms = ["Windows"] }
        ]);

        Assert.Equal(["Action", "Shooter"], result.Listing.Genres);
        Assert.Equal(["DOS", "Windows"], result.Listing.Platforms);
    }

    [Fact]
    public void Mirrors_are_always_additive()
    {
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1993) with
            {
                SourceKey = Primary,
                Downloads = [Download("https://a.test/doom.zip", sha1: "aa")]
            },
            Listing("Doom", 1993) with
            {
                SourceKey = Secondary,
                Downloads = [Download("https://b.test/doom.zip", sha1: "bb")]
            }
        ]);

        Assert.Equal(2, result.Listing.Downloads.Count);
        Assert.Equal([0, 1], result.Listing.Downloads.Select(download => download.MirrorRank));
        Assert.Equal(Primary, result.Listing.Downloads[0].SourceKey);
    }

    [Fact]
    public void The_same_mirror_reported_twice_is_stored_once()
    {
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1993) with { SourceKey = Primary, Downloads = [Download("https://a.test/doom.zip")] },
            Listing("Doom", 1993) with { SourceKey = Secondary, Downloads = [Download("https://a.test/doom.zip")] }
        ]);

        Assert.Single(result.Listing.Downloads);
    }

    [Fact]
    public void A_listing_with_no_downloadable_file_is_not_offered_for_install()
    {
        var result = Merger().Merge("lst_1", [Listing("Doom", 1993) with { Downloads = [] }]);

        Assert.False(result.Listing.IsDownloadable);
    }

    [Fact]
    public void One_source_restricting_access_does_not_restrict_anothers_copy()
    {
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1993) with
            {
                SourceKey = Primary,
                IsDownloadable = false,
                Downloads = [Download("https://a.test/doom.zip")]
            },
            Listing("Doom", 1993) with
            {
                SourceKey = Secondary,
                IsDownloadable = true,
                Downloads = [Download("https://b.test/doom.zip")]
            }
        ]);

        Assert.True(result.Listing.IsDownloadable);
    }

    [Fact]
    public void The_cover_is_taken_from_the_highest_ranked_source_that_has_one()
    {
        var result = Merger().Merge("lst_1",
        [
            Listing("Doom", 1993) with
            {
                SourceKey = Secondary,
                Images = [new ListingImageRef(new Uri("https://b.test/c.png"), ListingImageKind.Cover, 0, 0, 0)]
            },
            Listing("Doom", 1993) with
            {
                SourceKey = Primary,
                Images = [new ListingImageRef(new Uri("https://a.test/c.png"), ListingImageKind.Cover, 0, 0, 0)]
            }
        ]);

        Assert.Equal("https://a.test/c.png", result.Listing.CoverImageUrl);
        Assert.Equal(2, result.Listing.Images.Count);
    }

    [Fact]
    public void A_source_that_is_no_longer_registered_still_contributes_but_loses_ties()
    {
        // Removing a source must not delete the metadata it supplied, for the
        // same reason removing an achievement provider leaves its definitions.
        var result = Merger().Merge("lst_1",
        [
            Listing("Retired", 1993) with { SourceKey = "retired", Developer = "From a removed source" },
            Listing("Current", 1993) with { SourceKey = Primary, Developer = null }
        ]);

        Assert.Equal("Current", result.Listing.Title);
        Assert.Equal("From a removed source", result.Listing.Developer);
    }

    [Fact]
    public void The_trace_is_captured_only_when_asked_for()
    {
        var sources = new[]
        {
            Listing("Doom", 1993) with { SourceKey = Primary },
            Listing("Doom", 1994) with { SourceKey = Secondary }
        };

        Assert.Null(Merger().Merge("lst_1", sources).Trace);

        var traced = Merger().Merge("lst_1", sources, captureTrace: true).Trace;

        Assert.NotNull(traced);

        var yearDecisions = traced.Where(entry => entry.Field == nameof(CatalogListing.Year)).ToArray();

        Assert.Equal(2, yearDecisions.Length);
        Assert.Single(yearDecisions, entry => entry.Won);
        Assert.Equal("1993", yearDecisions.Single(entry => entry.Won).Value);
    }

    [Fact]
    public void Merging_is_deterministic_regardless_of_input_order()
    {
        var first = Listing("Doom", 1993) with { SourceKey = Primary, Developer = "id" };
        var second = Listing("Doom", 1994) with { SourceKey = Secondary, Description = "Longer text here." };

        var forward = Merger().Merge("lst_1", [first, second]);
        var reversed = Merger().Merge("lst_1", [second, first]);

        Assert.Equal(forward.Listing.ContentHash, reversed.Listing.ContentHash);
        Assert.Equal(forward.Listing.Title, reversed.Listing.Title);
        Assert.Equal(forward.Listing.Year, reversed.Listing.Year);
    }

    [Fact]
    public void The_content_hash_changes_when_content_does()
    {
        var baseline = Merger().Merge("lst_1", [Listing("Doom", 1993)]).Listing.ContentHash;
        var changed = Merger().Merge("lst_1", [Listing("Doom", 1993) with { Developer = "id" }])
            .Listing.ContentHash;

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Merging_nothing_is_rejected_rather_than_producing_an_empty_listing() =>
        Assert.Throws<ArgumentException>(() => Merger().Merge("lst_1", []));

    private static SourceListing Listing(string title, int? year) =>
        NormalizationTests.Build(title) with { SourceKey = Primary, Year = year };

    private static ListingDownloadRef Download(string url, string? sha1 = null) =>
        new() { Url = new Uri(url), Sha1 = sha1 };

    private static CatalogListing Merged(string title, int? year, [CallerLineNumber] int line = 0) => new()
    {
        ListingId = $"lst_{line:x8}{title.GetHashCode(StringComparison.Ordinal):x8}",
        Title = title,
        SortTitle = TitleNormalizer.ToSortTitle(title),
        Year = year,
        MatchKey = TitleNormalizer.ComputeMatchKey(title, year)
    };

    /// <summary>A source that exists only to carry a key and a rank.</summary>
    private sealed class StubSource(string key, int rank) : ICatalogSource
    {
        public string Key { get; } = key;

        public string DisplayName => Key;

        public int Rank { get; } = rank;

        public SourceThrottle Throttle => SourceThrottle.Polite;

        public bool IsAvailable => true;

        public IAsyncEnumerable<SourceListingRef> EnumerateAsync(
            SourceEnumerationOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SourceListing?> FetchAsync(
            SourceListingRef reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
