using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Sources;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the MyAbandonware parser against a page captured from the live site,
/// and the constraint that shapes the whole source: its download paths are
/// disallowed by robots.txt, so it contributes metadata only.
/// </summary>
public sealed class MyAbandonwareSourceTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "Discovery", "Fixtures", "myabandonware-game.html");

    private static readonly Uri PageAddress =
        new("https://www.myabandonware.com/game/snoopy-s-game-club-1id");

    [Fact]
    public void Structured_metadata_is_read_from_the_page_json_ld()
    {
        // The JSON-LD block is published for search engines, which gives the site
        // a strong reason to keep it correct — far more so than its class names.
        var listing = Map();

        Assert.NotNull(listing);
        Assert.Equal("Snoopy's Game Club", listing.Title);
        Assert.Equal(1992, listing.Year);
        Assert.Equal("Accolade, Inc.", listing.Developer);
        Assert.Equal("Accolade, Inc.", listing.Publisher);
        Assert.Equal(["Educational", "Puzzle"], listing.Genres);
        Assert.Equal(["DOS"], listing.Platforms);
    }

    [Fact]
    public void The_video_game_block_is_found_among_several_others()
    {
        // The page carries four JSON-LD blocks; only one is the game.
        var listing = Map();

        Assert.NotNull(listing);
        Assert.NotEqual("My Abandonware", listing.Title);
    }

    [Fact]
    public void No_download_address_is_ever_collected()
    {
        // The governing constraint. robots.txt disallows /download/* for every
        // crawler, and the fixture contains such a link precisely so that this
        // test would fail if the parser ever started following it.
        var listing = Map();

        Assert.NotNull(listing);
        Assert.Empty(listing.Downloads);
        Assert.False(listing.IsDownloadable);
        Assert.DoesNotContain("/download/", string.Join(" ", listing.Images.Select(i => i.Url.AbsoluteUri)));
    }

    [Fact]
    public void Template_marketing_text_is_not_imported_as_a_description()
    {
        // og:description here is boilerplate: "Remember X, an old video game from
        // 1992? Download it and play again on MyAbandonware." Importing it would
        // be worse than leaving the field empty, because the merge ranks
        // descriptions by length and this would beat a real one.
        var listing = Map();

        Assert.NotNull(listing);
        Assert.Null(listing.Description);
    }

    [Fact]
    public void Full_size_screenshots_are_preferred_over_thumbnails()
    {
        var listing = Map();

        Assert.NotNull(listing);

        var screenshots = listing.Images
            .Where(image => image.Kind == ListingImageKind.Screenshot)
            .ToArray();

        Assert.Equal(2, screenshots.Length);
        Assert.All(screenshots, image => Assert.DoesNotContain("/thumbs/", image.Url.AbsoluteUri));
        Assert.All(listing.Images, image => Assert.True(image.Url.IsAbsoluteUri));
    }

    [Fact]
    public void The_open_graph_image_becomes_the_cover()
    {
        var listing = Map();

        var cover = listing!.Images.Single(image => image.Kind == ListingImageKind.Cover);

        Assert.EndsWith("snoopys-game-club_6.png", cover.Url.AbsoluteUri);
    }

    [Fact]
    public void The_raw_page_is_kept_so_a_parser_fix_can_be_replayed_offline()
    {
        var listing = Map();

        Assert.NotNull(listing);
        Assert.Contains("VideoGame", listing.RawPayload);
    }

    [Fact]
    public void A_page_with_no_title_is_a_failure_rather_than_an_empty_record()
    {
        // What a site redesign looks like. Returning a blank listing would let it
        // pass for a successful import of nothing, which is what the pipeline's
        // health check exists to catch — and it only fires on nulls.
        var listing = MyAbandonwareCatalogSource.Map(
            "<html><head></head><body><p>nothing here</p></body></html>", "slug", PageAddress);

        Assert.Null(listing);
    }

    [Fact]
    public void A_page_without_json_ld_falls_back_to_the_markup()
    {
        // The fallback chain matters: JSON-LD is preferred, not required.
        var listing = MyAbandonwareCatalogSource.Map(
            """
            <html><head><meta property="og:title" content="Fallback Title"></head>
            <body><h1>Heading Title</h1></body></html>
            """,
            "slug",
            PageAddress);

        Assert.NotNull(listing);
        Assert.Equal("Fallback Title", listing.Title);
    }

    [Fact]
    public void Malformed_json_ld_does_not_stop_the_parse()
    {
        var listing = MyAbandonwareCatalogSource.Map(
            """
            <html><head>
            <script type="application/ld+json">{ this is not json </script>
            <meta property="og:title" content="Still Parsed">
            </head><body></body></html>
            """,
            "slug",
            PageAddress);

        Assert.NotNull(listing);
        Assert.Equal("Still Parsed", listing.Title);
    }

    [Theory]
    [InlineData("https://www.myabandonware.com/game/doom-1id", "doom-1id")]
    [InlineData("https://www.myabandonware.com/game/doom-1id/", "doom-1id")]
    [InlineData("https://www.myabandonware.com/game/doom-1id/play-1id", null)]
    [InlineData("https://www.myabandonware.com/browse/name/D", null)]
    [InlineData("https://www.myabandonware.com/", null)]
    public void Only_game_pages_are_enumerated(string url, string? expected) =>
        Assert.Equal(expected, MyAbandonwareCatalogSource.ExtractSlug(new Uri(url)));

    [Fact]
    public void The_source_is_off_until_it_is_switched_on_explicitly()
    {
        // Two switches, both off by default: discovery as a whole, and this
        // source in particular. A metadata-only source whose downloads are
        // off limits is a different proposition and deserves its own decision.
        var settings = new StubSettings();

        var source = new MyAbandonwareCatalogSource(
            new StubHttpClientFactory(),
            new AllowAllRobots(),
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MyAbandonwareCatalogSource>.Instance);

        Assert.False(source.IsAvailable);

        settings.Set(settings.Current with { DiscoveryEnabled = true });
        Assert.False(source.IsAvailable);

        settings.Set(settings.Current with { MyAbandonwareEnabled = true });
        Assert.True(source.IsAvailable);
    }

    [Fact]
    public void The_throttle_is_spaced_because_this_is_a_small_site()
    {
        var source = new MyAbandonwareCatalogSource(
            new StubHttpClientFactory(),
            new AllowAllRobots(),
            new StubSettings(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MyAbandonwareCatalogSource>.Instance);

        Assert.Equal(1, source.Throttle.MaxConcurrency);
        Assert.True(source.Throttle.MinimumInterval >= TimeSpan.FromSeconds(1));
    }

    private static SourceListing? Map() =>
        MyAbandonwareCatalogSource.Map(File.ReadAllText(FixturePath), "snoopy-s-game-club-1id", PageAddress);

    /// <summary>Settings held in memory, with no file behind them.</summary>
    private sealed class StubSettings : GameLauncher.Desktop.Services.Settings.ISettingsService
    {
        public GameLauncher.Desktop.Models.AppSettings Current { get; private set; } = new();

        public event EventHandler<GameLauncher.Desktop.Models.AppSettings>? SettingsChanged;

        public void Set(GameLauncher.Desktop.Models.AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
        }

        public Task<GameLauncher.Desktop.Models.AppSettings> LoadAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(
            GameLauncher.Desktop.Models.AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            Set(settings);
            return Task.CompletedTask;
        }
    }

    /// <summary>A robots policy that permits everything, for tests that are not about it.</summary>
    private sealed class AllowAllRobots : GameLauncher.Desktop.Services.Discovery.Http.IRobotsPolicy
    {
        public Task<bool> IsAllowedAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<TimeSpan?> GetCrawlDelayAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(null);
    }
}
