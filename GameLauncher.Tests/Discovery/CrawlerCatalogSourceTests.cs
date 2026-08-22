using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Import;
using GameLauncher.Desktop.Services.Discovery.Sources.Crawler;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the crawler as a catalogue source, end to end through the importer.
/// </summary>
/// <remarks>
/// The join between the two halves: a crawl produces observations, and the
/// existing import pipeline normalises, matches, merges and stores them. Nothing
/// here re-tests the pipeline — it tests that a crawled site arrives in it in the
/// shape it expects.
/// </remarks>
public sealed class CrawlerCatalogSourceTests
{
    private const string Sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    /// <summary>A listing page linking to detail pages.</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>HTML.</returns>
    private static string Listing(params (string Href, string Title)[] entries) =>
        "<!doctype html><html><body><main>" +
        string.Concat(entries.Select(entry =>
            $"<article class='game'><h2><a href='{entry.Href}'>{entry.Title}</a></h2></article>")) +
        "</main></body></html>";

    /// <summary>A detail page with the fields a crawl reads.</summary>
    /// <param name="title">The game's name.</param>
    /// <param name="extra">Extra markup, such as a download link.</param>
    /// <returns>HTML.</returns>
    private static string Detail(string title, string extra = "") =>
        $"""
         <!doctype html><html><head>
         <title>{title} | Example Games</title>
         <meta property="og:description" content="A description of {title}.">
         </head><body><main>
         <h1 class="entry-title">{title}</h1>
         <img class="cover" src="/img/{title}.png">
         <dl><dt>Developer</dt><dd>Example Studio</dd>
             <dt>Publisher</dt><dd>Example Publishing</dd></dl>
         <time datetime="1993-12-10">December 1993</time>
         {extra}
         </main></body></html>
         """;

    /// <summary>Writes a crawling manifest into the adapter folder.</summary>
    /// <param name="host">The container under test.</param>
    /// <param name="site">The site to crawl.</param>
    /// <param name="extra">Extra YAML appended at the top level.</param>
    /// <returns>A task that completes when the file is written.</returns>
    private static async Task WriteManifestAsync(
        TestAppHost host,
        LoopbackSiteServer site,
        string extra = "")
    {
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        var yaml =
            $"""
             key: example-site
             displayName: Example Site
             enabled: true
             crawler:
               url: {site.Url("/games/")}
               allowPrivateHosts: true
               delayMilliseconds: 0
               maxPages: 10
             {extra}

             """;

        await File.WriteAllTextAsync(Path.Combine(directory, "example-site.yaml"), yaml);

        // Discovery is opt-in, and a source reports itself unavailable while it
        // is off — which is the behaviour, not an inconvenience to work around.
        var settings = host.Resolve<ISettingsService>();

        await settings.SaveAsync(settings.Current with { DiscoveryEnabled = true });
    }

    /// <summary>Builds a top-level sourcing block for a manifest.</summary>
    /// <param name="extra">An extra line inside the block, already unindented.</param>
    /// <returns>YAML, indented to sit at the manifest's top level.</returns>
    private static string SourcingBlock(string extra = "") =>
        string.Join(
            Environment.NewLine,
            "sourcing:",
            "  enabled: true",
            "  strategy: direct-link",
            "  allowPrivateHosts: true",
            string.IsNullOrWhiteSpace(extra) ? "" : "  " + extra);

    /// <summary>Builds the source over a host's real services.</summary>
    /// <param name="host">The container under test.</param>
    /// <returns>The source.</returns>
    private static CrawlerCatalogSource Source(TestAppHost host) =>
        host.Resolve<IEnumerable<ICatalogSource>>().OfType<CrawlerCatalogSource>().Single();

    /// <summary>Runs an import limited to the crawler source.</summary>
    /// <param name="host">The container under test.</param>
    /// <returns>What the pass produced.</returns>
    private static Task<ImportRunResult> ImportAsync(TestAppHost host) =>
        host.Resolve<ICatalogImportService>().RunAsync(
            new ImportRunOptions { SourceKeys = [CrawlerCatalogSource.SourceKey] });

    [Fact]
    public async Task A_crawled_site_becomes_catalogue_listings()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));
        site.AddPage("/g/doom", Detail("Doom"));
        site.AddPage("/g/quake", Detail("Quake"));

        await WriteManifestAsync(host, site);

        var result = await ImportAsync(host);

        Assert.Equal(2, result.ListingsAdded);

        var page = await host.Resolve<ICatalogListingRepository>()
            .QueryAsync(new CatalogListingQuery { Take = 50 });

        Assert.Equal(["Doom", "Quake"], page.Items.Select(item => item.Title).Order());
    }

    [Fact]
    public async Task Metadata_is_read_off_the_detail_pages()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));
        site.AddPage("/g/doom", Detail("Doom"));
        site.AddPage("/g/quake", Detail("Quake"));

        await WriteManifestAsync(host, site);
        await ImportAsync(host);

        var repository = host.Resolve<ICatalogListingRepository>();
        var page = await repository.QueryAsync(new CatalogListingQuery { Take = 50 });
        var doom = page.Items.Single(item => item.Title == "Doom");
        var listing = await repository.GetAsync(doom.ListingId);

        Assert.NotNull(listing);
        Assert.Equal(1993, listing.Year);
        Assert.Equal("Example Studio", listing.Developer);
        Assert.Equal("Example Publishing", listing.Publisher);
        Assert.Contains("A description of Doom", listing.Description ?? string.Empty, StringComparison.Ordinal);

        // The cover came off the page and was resolved to an absolute address.
        Assert.NotNull(listing.CoverImageUrl);
        Assert.Contains("/img/", listing.CoverImageUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalogue_only_site_produces_listings_with_no_addresses()
    {
        // The separation the whole design rests on: a site can be worth indexing
        // without being somewhere anything is fetched from.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));
        site.AddPage("/g/doom", Detail("Doom"));
        site.AddPage("/g/quake", Detail("Quake"));

        await WriteManifestAsync(host, site);
        await ImportAsync(host);

        var repository = host.Resolve<ICatalogListingRepository>();
        var page = await repository.QueryAsync(new CatalogListingQuery { Take = 50 });
        var listing = await repository.GetAsync(page.Items[0].ListingId);

        Assert.NotNull(listing);
        Assert.Empty(listing.Downloads);
    }

    [Fact]
    public async Task Eager_resolution_records_addresses_during_the_import()
    {
        // Free for direct-link: the page the addresses are on is the page the
        // crawl just parsed, so no extra request is made.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));

        site.AddPage("/g/doom", Detail("Doom",
            $"<a href='/files/doom.zip'>Download</a><code>{Sha256}</code>"));

        site.AddPage("/g/quake", Detail("Quake", "<a href='/files/quake.zip'>Download</a>"));

        await WriteManifestAsync(host, site, SourcingBlock("resolution: eager"));

        await ImportAsync(host);

        var repository = host.Resolve<ICatalogListingRepository>();
        var page = await repository.QueryAsync(new CatalogListingQuery { Take = 50 });
        var doom = page.Items.Single(item => item.Title == "Doom");
        var listing = await repository.GetAsync(doom.ListingId);

        Assert.NotNull(listing);

        var download = Assert.Single(listing.Downloads);

        Assert.EndsWith("/files/doom.zip", download.Url, StringComparison.Ordinal);
        Assert.Equal(Sha256, download.Sha256);

        // And the listing is offered for installation because it has one.
        Assert.True(listing.IsDownloadable);
    }

    [Fact]
    public async Task Lazy_resolution_leaves_the_addresses_until_install()
    {
        // The default, and the reason it is: a catalogue of several thousand
        // games would otherwise cost several thousand extra fetches per import.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));
        site.AddPage("/g/doom", Detail("Doom", "<a href='/files/doom.zip'>Download</a>"));
        site.AddPage("/g/quake", Detail("Quake", "<a href='/files/quake.zip'>Download</a>"));

        await WriteManifestAsync(host, site, SourcingBlock());

        await ImportAsync(host);

        var repository = host.Resolve<ICatalogListingRepository>();
        var page = await repository.QueryAsync(new CatalogListingQuery { Take = 50 });
        var listing = await repository.GetAsync(page.Items[0].ListingId);

        Assert.NotNull(listing);
        Assert.Empty(listing.Downloads);
    }

    [Fact]
    public async Task Detail_pages_can_be_skipped_for_a_fast_thin_pass()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));

        await WriteManifestAsync(host, site);

        // Turning detail reads off is a crawler setting, so it belongs inside
        // the crawler block the helper already wrote.
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;
        var path = Path.Combine(directory, "example-site.yaml");
        var yaml = await File.ReadAllTextAsync(path);

        await File.WriteAllTextAsync(
            path,
            yaml.Replace(
                "  maxPages: 10",
                "  maxPages: 10" + Environment.NewLine + "  readDetailPages: false"));

        await ImportAsync(host);

        var page = await host.Resolve<ICatalogListingRepository>()
            .QueryAsync(new CatalogListingQuery { Take = 50 });

        // Titles and addresses, from the listing page alone.
        Assert.Equal(["Doom", "Quake"], page.Items.Select(item => item.Title).Order());

        // The detail pages were never requested.
        Assert.DoesNotContain("/g/doom", site.Requests);
    }

    [Fact]
    public async Task A_second_import_recognises_what_it_already_stored()
    {
        // The identity has to survive a re-crawl, or every pass duplicates the
        // whole catalogue.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));
        site.AddPage("/g/doom", Detail("Doom"));
        site.AddPage("/g/quake", Detail("Quake"));

        await WriteManifestAsync(host, site);

        await ImportAsync(host);
        var second = await ImportAsync(host);

        Assert.Equal(0, second.ListingsAdded);

        var page = await host.Resolve<ICatalogListingRepository>()
            .QueryAsync(new CatalogListingQuery { Take = 50 });

        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task A_source_is_unavailable_while_discovery_is_off()
    {
        // Nothing is fetched until someone asks, which is the rule the whole
        // discovery half follows.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));

        await WriteManifestAsync(host, site);

        var settings = host.Resolve<ISettingsService>();

        await settings.SaveAsync(settings.Current with { DiscoveryEnabled = false });

        Assert.False(Source(host).IsAvailable);
    }

    [Fact]
    public async Task A_manifest_with_no_crawler_section_is_ignored_by_this_source()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(Path.Combine(directory, "sourcing-only.yaml"),
            $"""
             key: sourcing-only
             match:
               hosts: ["{site.BaseAddress.Host}"]
             sourcing:
               enabled: true
               strategy: direct-link

             """);

        var settings = host.Resolve<ISettingsService>();

        await settings.SaveAsync(settings.Current with { DiscoveryEnabled = true });

        // The manifest loads and is perfectly valid; it simply describes no
        // catalogue, so this source has nothing to do.
        var manifests = await host.Resolve<GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable
            .IFeedManifestStore>().GetAsync();

        Assert.Single(manifests);
        Assert.False(manifests[0].ProvidesCrawler);
        Assert.True(manifests[0].ProvidesSourcing);
        Assert.False(Source(host).IsAvailable);
    }

    [Fact]
    public async Task The_shipped_crawler_example_is_valid_even_though_it_ships_disabled()
    {
        // It ships off because example.test is not a real site, so the loader
        // passes over it and the "every example loads" test never sees it.
        // Without this, the example a user is most likely to copy is the one
        // nothing checks.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        var text = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "crawled-site.yaml"));

        Assert.Contains("enabled: false", text, StringComparison.Ordinal);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "crawled-site.yaml"),
            text.Replace("enabled: false", "enabled: true", StringComparison.Ordinal));

        var manifest = Assert.Single(
            await host.Resolve<GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable
                .IFeedManifestStore>().GetAsync());

        Assert.Equal("example-site", manifest.Key);
        Assert.Empty(manifest.Validate());

        // Both halves, and the documented spellings of the enums actually parse.
        Assert.True(manifest.ProvidesCrawler);
        Assert.True(manifest.ProvidesSourcing);

        Assert.Equal(
            GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable.SourcingStrategy.DirectLink,
            manifest.Sourcing!.Strategy);

        Assert.Equal(
            GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable.SourcingResolution.Lazy,
            manifest.Sourcing.Resolution);

        Assert.Equal(100, manifest.SourcingPriority);
    }

    [Fact]
    public async Task Diagnostics_are_available_after_a_pass()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(("/g/doom", "Doom"), ("/g/quake", "Quake")));
        site.AddPage("/g/doom", Detail("Doom"));
        site.AddPage("/g/quake", Detail("Quake"));

        await WriteManifestAsync(host, site);
        await ImportAsync(host);

        var diagnostics = Source(host).LastDiagnostics;

        Assert.True(diagnostics.PagesFetched > 0);
        Assert.Equal(2, diagnostics.ItemsFound);
        Assert.True(diagnostics.LooksHealthy);
    }

    [Fact]
    public async Task A_site_that_cannot_be_read_reports_rather_than_throws()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        // No listing page registered, so the site answers 404.
        await WriteManifestAsync(host, site);

        var result = await ImportAsync(host);

        Assert.Equal(0, result.ListingsAdded);

        var crawled = result.Sources.SingleOrDefault(
            source => source.SourceKey == CrawlerCatalogSource.SourceKey);

        Assert.NotNull(crawled);
        Assert.Equal(0, crawled!.ItemsSeen);
    }
}
