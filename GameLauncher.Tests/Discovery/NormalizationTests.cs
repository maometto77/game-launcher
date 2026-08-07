using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Normalization;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the rules that decide when two spellings mean the same game. Each one
/// trades recall against the risk of merging distinct titles, so the tests that
/// assert something is <em>not</em> collapsed matter as much as the ones that
/// assert it is.
/// </summary>
public sealed class NormalizationTests
{
    private static readonly ListingNormalizer Normalizer = new();

    [Theory]
    [InlineData("Oregon Trail, The", "The Oregon Trail")]
    [InlineData("Secret of Monkey Island, The", "The Secret of Monkey Island")]
    [InlineData("Incredible Machine, A", "A Incredible Machine")]
    [InlineData("Doom", "Doom")]
    [InlineData("Command & Conquer, Red Alert", "Command & Conquer, Red Alert")]
    public void Catalogue_style_trailing_articles_are_restored(string stored, string expected) =>
        Assert.Equal(expected, TitleNormalizer.RestoreLeadingArticle(stored));

    [Theory]
    [InlineData("Oregon Trail, The", "Oregon Trail")]
    [InlineData("The Secret of Monkey Island", "Secret of Monkey Island")]
    [InlineData("Doom", "Doom")]
    public void Sort_titles_drop_the_leading_article(string title, string expected) =>
        Assert.Equal(expected, TitleNormalizer.ToSortTitle(title));

    [Fact]
    public void Diacritics_do_not_prevent_a_match() =>
        Assert.Equal(TitleNormalizer.Normalize("Pokemon"), TitleNormalizer.Normalize("Pokémon"));

    [Theory]
    [InlineData("Civilization II", "Civilization 2")]
    [InlineData("Final Fantasy VII", "Final Fantasy 7")]
    [InlineData("Might and Magic III", "Might and Magic 3")]
    public void Roman_numerals_match_their_arabic_form(string roman, string arabic) =>
        Assert.Equal(TitleNormalizer.Normalize(arabic), TitleNormalizer.Normalize(roman));

    [Fact]
    public void A_single_letter_is_never_read_as_a_roman_numeral()
    {
        // "x com" becoming "10 com" would be worse than a missed match: it would
        // match anything else that normalised to the same nonsense.
        Assert.Equal("x com", TitleNormalizer.Normalize("X-COM"));
        Assert.Equal("rocky v", TitleNormalizer.Normalize("Rocky V"));
    }

    [Theory]
    [InlineData("Doom Gold Edition")]
    [InlineData("Doom CD Version")]
    [InlineData("Doom Special Edition")]
    [InlineData("Doom (1993)")]
    [InlineData("Doom v1.2")]
    [InlineData("Doom [CD]")]
    [InlineData("Doom Gold Edition CD Version")]
    public void Packaging_markers_do_not_prevent_a_match(string decorated) =>
        Assert.Equal(TitleNormalizer.Normalize("Doom"), TitleNormalizer.Normalize(decorated));

    [Theory]
    [InlineData("Doom Demo")]
    [InlineData("Doom Shareware")]
    [InlineData("Doom Beta")]
    public void A_materially_different_release_is_kept_distinct(string release)
    {
        // Collapsing these would attribute one release's download links to
        // another, which is a worse failure than showing two catalogue entries.
        Assert.NotEqual(TitleNormalizer.Normalize("Doom"), TitleNormalizer.Normalize(release));
    }

    [Fact]
    public void The_match_key_carries_the_year_so_a_remake_cannot_absorb_the_original()
    {
        var original = TitleNormalizer.ComputeMatchKey("Prince of Persia", 1989);
        var remake = TitleNormalizer.ComputeMatchKey("Prince of Persia", 2008);

        Assert.NotEqual(original, remake);
        Assert.Equal("prince of persia", TitleNormalizer.ComputeTitleKey("Prince of Persia"));
    }

    [Theory]
    [InlineData(1990, 1990)]
    [InlineData(1200, null)]
    [InlineData(3000, null)]
    public void Implausible_years_are_discarded_rather_than_carried(int input, int? expected)
    {
        var listing = Build("Doom") with { Year = input };

        Assert.Equal(expected, Normalizer.Normalize(listing).Year);
    }

    [Fact]
    public void A_comma_separated_genre_field_is_split_but_a_slashed_one_is_not()
    {
        // The Internet Archive really does store "Educational, Simulation" in one
        // field, while "Racing / Driving" is a single MobyGames genre.
        Assert.Equal(["Educational", "Simulation"], GenreVocabulary.Map("Educational, Simulation"));
        Assert.Equal(["Racing"], GenreVocabulary.Map("Racing / Driving"));
    }

    [Theory]
    [InlineData("RPG")]
    [InlineData("Role-Playing (RPG)")]
    [InlineData("role playing")]
    public void Genre_synonyms_collapse_to_one_facet(string spelling) =>
        Assert.Equal(["Role-Playing"], GenreVocabulary.Map(spelling));

    [Fact]
    public void An_unrecognised_genre_is_kept_rather_than_discarded() =>
        Assert.Equal(["Interactive Fiction"], GenreVocabulary.Map("interactive fiction"));

    [Theory]
    [InlineData("MicroProse Software, Inc.")]
    [InlineData("MicroProse Software Inc")]
    [InlineData("MICROPROSE SOFTWARE")]
    public void Legal_entity_suffixes_do_not_split_one_company_into_several(string spelling) =>
        Assert.Equal(
            CompanyNormalizer.Normalize("MicroProse Software"),
            CompanyNormalizer.Normalize(spelling));

    [Fact]
    public void Descriptive_words_are_kept_so_distinct_companies_stay_distinct()
    {
        // Stripping "Games" and "Entertainment" would fold every company sharing
        // a first word into one, misattributing whole back catalogues.
        Assert.NotEqual(
            CompanyNormalizer.Normalize("Epic Games"),
            CompanyNormalizer.Normalize("Epic Megagames"));

        Assert.NotEqual(
            CompanyNormalizer.Normalize("Access Software"),
            CompanyNormalizer.Normalize("Access Associates"));
    }

    [Fact]
    public void Html_descriptions_become_plain_text()
    {
        var listing = Build("Doom") with
        {
            Description = "<p>A <b>first-person</b> shooter.</p><p>Id&nbsp;Software &amp; friends.</p>"
        };

        var normalized = Normalizer.Normalize(listing).Description;

        Assert.NotNull(normalized);
        Assert.DoesNotContain('<', normalized);
        Assert.Contains("first-person shooter", normalized);
        Assert.Contains("Id Software & friends", normalized);
    }

    [Fact]
    public void Duplicate_downloads_and_images_are_collapsed_by_address()
    {
        var listing = Build("Doom") with
        {
            Downloads =
            [
                new ListingDownloadRef { Url = new Uri("https://example.test/doom.zip") },
                new ListingDownloadRef { Url = new Uri("https://example.test/doom.zip") }
            ],
            Images =
            [
                new ListingImageRef(new Uri("https://example.test/c.png"), ListingImageKind.Cover, 0, 0, 0),
                new ListingImageRef(new Uri("https://example.test/c.png"), ListingImageKind.Cover, 0, 0, 1)
            ]
        };

        var normalized = Normalizer.Normalize(listing);

        Assert.Single(normalized.Downloads);
        Assert.Single(normalized.Images);
    }

    [Fact]
    public void Normalisation_is_idempotent()
    {
        var listing = Build("Oregon Trail, The") with
        {
            Year = 1990,
            Description = "<p>Hello</p>",
            Developer = "MECC, Inc.",
            Genres = ["Educational, Simulation"]
        };

        var once = Normalizer.Normalize(listing);
        var twice = Normalizer.Normalize(once);

        Assert.Equal(once.Title, twice.Title);
        Assert.Equal(once.Description, twice.Description);
        Assert.Equal(once.Developer, twice.Developer);
        Assert.Equal(once.Genres, twice.Genres);
    }

    internal static SourceListing Build(string title) => new()
    {
        SourceKey = "test",
        SourceItemId = title,
        SourceUrl = new Uri("https://example.test/" + Uri.EscapeDataString(title)),
        Title = title,
        RawPayload = "{}"
    };
}
