using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Import;
using GameLauncher.Desktop.Services.Discovery.Sources.Scriptable;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the half of a feed manifest that fills the catalogue, as opposed to
/// the half that resolves downloads for listings something else found.
/// </summary>
public sealed class ScriptableCatalogTests
{
    [Fact]
    public void A_catalogue_only_manifest_needs_no_sourcing_fields()
    {
        // The two halves are independent. Demanding a 'map.url' of a feed that
        // only lists games would mean writing an address nothing ever reads.
        var manifest = new FeedManifest
        {
            Key = "catalogue-only",
            Catalog = new FeedCatalog
            {
                Request = new FeedRequest { Url = "catalog.json" },
                Map = new FeedCatalogMap { Title = "title" }
            }
        };

        Assert.Empty(manifest.Validate());
        Assert.True(manifest.ProvidesCatalog);
    }

    [Fact]
    public async Task A_sourcing_only_manifest_is_not_rejected_for_lacking_a_catalogue()
    {
        // A regression guard with a real incident behind it. Adding the catalogue
        // half started rejecting every manifest that had only the sourcing half,
        // with "'catalog.request.url' is required" — because the deserialiser
        // materialises an empty section for a key the file never mentioned, and
        // validation could not tell that from a section written and left blank.
        // Four working manifests stopped loading at once.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "sourcing-only.yaml"),
            """
            key: sourcing-only
            displayName: Sourcing only
            match:
              hosts: [example.test]
            request:
              url: https://example.test/api/{slug}
            format: json
            items: files
            map:
              url: url
              fileName: name

            """);

        var manifest = Assert.Single(await host.Resolve<IFeedManifestStore>().GetAsync());

        Assert.Equal("sourcing-only", manifest.Key);
        Assert.Empty(manifest.Validate());

        // And it must not advertise itself as a catalogue source, or the import
        // would enumerate a feed that has nothing to enumerate.
        Assert.False(manifest.ProvidesCatalog);
    }

    [Fact]
    public async Task A_crawler_only_manifest_does_not_reach_the_feed_reader()
    {
        // A regression guard with a real incident behind it. Adding the crawler
        // made ProvidesCatalog mean "fills the catalogue somehow", which handed
        // the feed reader manifests that have no catalog section at all — and it
        // dereferenced one, throwing a NullReferenceException that was outside
        // the caught set and took every other custom feed down with it.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "crawler-only.yaml"),
            """
            key: crawler-only
            displayName: Crawler only
            crawler:
              url: https://example.test/games/

            """);

        var manifest = Assert.Single(await host.Resolve<IFeedManifestStore>().GetAsync());

        Assert.Empty(manifest.Validate());
        Assert.True(manifest.ProvidesCrawler);

        // The crawler fills the catalogue through its own source. This one has
        // nothing to read.
        Assert.False(manifest.ProvidesCatalog);

        var source = Source(host);

        Assert.False(source.IsAvailable);

        // And enumerating anyway must be empty rather than explosive.
        var seen = new List<SourceListingRef>();

        await foreach (var reference in source.EnumerateAsync(new SourceEnumerationOptions()))
        {
            seen.Add(reference);
        }

        Assert.Empty(seen);
    }

    [Fact]
    public void A_manifest_that_does_nothing_at_all_is_refused()
    {
        var problems = new FeedManifest { Key = "empty" }.Validate();

        Assert.Contains(problems, problem => problem.Contains("must do something", StringComparison.Ordinal));
    }

    [Fact]
    public void A_catalogue_section_still_needs_somewhere_to_read_and_a_title()
    {
        var problems = new FeedManifest { Key = "half", Catalog = new FeedCatalog() }.Validate();

        Assert.Contains(problems, problem => problem.Contains("catalog.request.url", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("catalog.map.title", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_local_catalogue_file_becomes_listings()
    {
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(Path.Combine(directory, "shelf.yaml"), CatalogManifest("shelf"));
        await File.WriteAllTextAsync(Path.Combine(directory, "games.json"), Payload());

        var source = Source(host);
        var references = new List<SourceListingRef>();

        await foreach (var reference in source.EnumerateAsync(new SourceEnumerationOptions()))
        {
            references.Add(reference);
        }

        Assert.Equal(2, references.Count);

        var listing = await source.FetchAsync(references[0]);

        Assert.NotNull(listing);
        Assert.Equal("Doom", listing.Title);
        Assert.Equal(1993, listing.Year);
        Assert.Equal("id Software", listing.Developer);
        Assert.Equal("https://archive.org/details/msdos_Doom_1993", listing.SourceUrl.AbsoluteUri);

        // Attributed to the manifest, not to the family of them. Someone reading
        // the card needs to know which feed said so.
        Assert.Equal("shelf", listing.SourceKey);
    }

    [Fact]
    public async Task A_catalogue_feed_carries_its_digest_and_size_onto_the_download()
    {
        // Without this the addresses arrive unverifiable: a feed can publish a
        // SHA-1 and a byte count, and if the catalogue half has nowhere to put
        // them the download service checks nothing.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "verified.yaml"),
            """
            key: verified
            catalog:
              request:
                url: games.json
              items: results
              map:
                title: title
                id: source_id
                page: url
                downloadUrl: download_url
                sha1: sha1
                sizeBytes: size_bytes

            """);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "games.json"),
            """
            { "results": [
              { "title": "Alice", "source_id": "alice",
                "url": "https://archive.org/details/alice",
                "download_url": "https://archive.org/download/alice/alice.zip",
                "sha1": "62739d2989cda3facb92304251ccb4e60735dcdd",
                "size_bytes": 989711238 } ] }
            """);

        var source = Source(host);
        var reference = await SingleAsync(source);
        var listing = await source.FetchAsync(reference);

        Assert.NotNull(listing);

        var download = Assert.Single(listing.Downloads);

        Assert.Equal("62739d2989cda3facb92304251ccb4e60735dcdd", download.Sha1);
        Assert.Equal(989711238, download.SizeBytes);
        Assert.Equal(DownloadKind.Game, download.Kind);
    }

    [Fact]
    public async Task A_catalogue_feed_publishing_a_nonsense_digest_is_left_unverified()
    {
        // Same rule the sourcing half follows: a field holding "unknown" would
        // fail every transfer with a mismatch that is really a feed typo.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "sloppy.yaml"),
            """
            key: sloppy
            catalog:
              request:
                url: games.json
              items: results
              map:
                title: title
                id: source_id
                page: url
                downloadUrl: download_url
                sha1: sha1

            """);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "games.json"),
            """
            { "results": [
              { "title": "Alice", "source_id": "alice",
                "url": "https://archive.org/details/alice",
                "download_url": "https://archive.org/download/alice/alice.zip",
                "sha1": "unknown" } ] }
            """);

        var source = Source(host);
        var listing = await source.FetchAsync(await SingleAsync(source));

        Assert.NotNull(listing);
        Assert.Null(Assert.Single(listing.Downloads).Sha1);
    }

    [Theory]
    [InlineData("2026-01-17T16:44:40Z", 2026)]
    [InlineData("2025-10-04 16:52:20", 2025)]
    [InlineData("1993", 1993)]
    public async Task A_publication_date_supplies_the_year_and_the_change_stamp(string published, int year)
    {
        // Both spellings the Archive itself uses, plus the bare year some feeds
        // put where a date belongs. Mapping `year` at any of these yields
        // nothing, because a year is parsed as a number and a timestamp is not.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "dated.yaml"),
            """
            key: dated
            catalog:
              request:
                url: games.json
              items: results
              map:
                title: title
                id: source_id
                page: url
                pubDate: pub_date

            """);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "games.json"),
            $$"""
              { "results": [
                { "title": "Doom", "source_id": "d",
                  "url": "https://archive.org/details/d",
                  "pub_date": "{{published}}" } ] }
              """);

        var source = Source(host);
        var listing = await source.FetchAsync(await SingleAsync(source));

        Assert.NotNull(listing);
        Assert.Equal(year, listing.Year);

        // Also the change stamp, which is what lets an incremental pass skip an
        // entry that has not moved.
        Assert.NotNull(listing.SourceUpdatedAt);
        Assert.Equal(year, listing.SourceUpdatedAt!.Value.Year);
    }

    [Fact]
    public async Task A_mapped_year_is_not_overridden_by_a_publication_date()
    {
        // The year is what the feed says the game is from; the date is only
        // when the entry was posted, which for a preservation archive is
        // decades later. Letting the second win would date every game to its
        // upload.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "dated.yaml"),
            """
            key: dated
            catalog:
              request:
                url: games.json
              items: results
              map:
                title: title
                id: source_id
                page: url
                year: year
                pubDate: pub_date

            """);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "games.json"),
            """
            { "results": [
              { "title": "Doom", "source_id": "d", "year": 1993,
                "url": "https://archive.org/details/d",
                "pub_date": "2026-01-17T16:44:40Z" } ] }
            """);

        var source = Source(host);
        var listing = await source.FetchAsync(await SingleAsync(source));

        Assert.NotNull(listing);
        Assert.Equal(1993, listing.Year);
        Assert.Equal(2026, listing.SourceUpdatedAt!.Value.Year);
    }

    [Fact]
    public async Task An_unreadable_publication_date_is_discarded_rather_than_guessed()
    {
        // A wrong date becomes a wrong year, and a wrong year makes the matcher
        // treat one game as a different release.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "dated.yaml"),
            """
            key: dated
            catalog:
              request:
                url: games.json
              items: results
              map:
                title: title
                id: source_id
                page: url
                pubDate: pub_date

            """);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "games.json"),
            """
            { "results": [
              { "title": "Doom", "source_id": "d",
                "url": "https://archive.org/details/d",
                "pub_date": "sometime last spring" } ] }
            """);

        var source = Source(host);
        var listing = await source.FetchAsync(await SingleAsync(source));

        Assert.NotNull(listing);
        Assert.Null(listing.Year);
        Assert.Null(listing.SourceUpdatedAt);
    }

    [Fact]
    public async Task A_page_template_turns_an_identifier_into_an_address()
    {
        // The common shape: a feed publishing ids rather than addresses. The
        // mapping language walks a payload, so only a template can join them.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "shelf.yaml"),
            """
            key: shelf
            catalog:
              request:
                url: games.json
              items: games
              pageTemplate: https://archive.org/details/{id}
              map:
                title: title
                id: id

            """);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "games.json"),
            """{ "games": [ { "id": "a weird/id", "title": "Doom" } ] }""");

        // One instance throughout: the fetch is answered from the enumeration
        // that produced the reference, which is the point of doing it that way.
        var source = Source(host);

        var reference = await SingleAsync(source);
        var listing = await source.FetchAsync(reference);

        // Escaped as it is substituted, so an identifier with a slash in it
        // cannot invent a path segment.
        Assert.NotNull(listing);
        Assert.Equal("https://archive.org/details/a%20weird%2Fid", listing.SourceUrl.AbsoluteUri);
    }

    [Fact]
    public async Task An_item_with_no_usable_page_is_passed_over()
    {
        // The address is what the sourcing adapters dispatch on. A listing with
        // none can never be installed, so it is not worth a card.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(Path.Combine(directory, "shelf.yaml"), CatalogManifest("shelf"));

        await File.WriteAllTextAsync(
            Path.Combine(directory, "games.json"),
            """
            { "games": [
              { "id": "x", "title": "Nowhere" },
              { "id": "y", "title": "Doom", "page": "https://archive.org/details/msdos_Doom_1993" } ] }
            """);

        var reference = await SingleAsync(Source(host));

        Assert.Equal("shelf|y", reference.SourceItemId);
    }

    [Fact]
    public async Task A_manifest_the_robots_rules_forbid_contributes_nothing()
    {
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "remote.yaml"),
            """
            key: remote
            catalog:
              request:
                url: https://example.test/catalog.json
              items: games
              map:
                title: title

            """);

        var source = Source(host, robotsAllow: false);
        var count = 0;

        await foreach (var _ in source.EnumerateAsync(new SourceEnumerationOptions()))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task The_shipped_catalogue_example_and_its_payload_agree()
    {
        // The files a user is told to copy, read by the code that will read
        // them. Documentation that has drifted from the implementation is worse
        // than none.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        foreach (var name in new[] { "local-catalog.yaml", "catalog.json" })
        {
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "AdapterExamples", name),
                Path.Combine(directory, name));
        }

        var titles = new List<string>();

        await foreach (var reference in Source(host).EnumerateAsync(new SourceEnumerationOptions()))
        {
            titles.Add(reference.Title);
        }

        Assert.Equal(["Doom", "Prince of Persia"], titles);
    }

    [Fact]
    public async Task Two_feeds_naming_one_game_produce_a_single_card()
    {
        // The whole point of the exercise. Two manifests describe the same game
        // under titles that differ by an edition marker, and the catalogue ends
        // up with one listing carrying both sources — which is what the badges
        // on a card are drawn from.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;
        var settings = host.Resolve<ISettingsService>();

        await settings.SaveAsync(settings.Current with { DiscoveryEnabled = true });

        await File.WriteAllTextAsync(Path.Combine(directory, "one.yaml"), CatalogManifest("one", "one.json"));
        await File.WriteAllTextAsync(Path.Combine(directory, "two.yaml"), CatalogManifest("two", "two.json"));

        await File.WriteAllTextAsync(
            Path.Combine(directory, "one.json"),
            """
            { "games": [
              { "id": "d", "title": "Doom", "year": 1993,
                "page": "https://archive.org/details/msdos_Doom_1993",
                "download": "https://one.test/doom.zip" } ] }
            """);

        // The same game. 'Gold Edition' is an edition marker and the normaliser
        // folds it away before the match is attempted, which is why this lands
        // on the first listing rather than beside it.
        await File.WriteAllTextAsync(
            Path.Combine(directory, "two.json"),
            """
            { "games": [
              { "id": "d2", "title": "DOOM Gold Edition", "year": 1993,
                "page": "https://other.test/games/doom",
                "download": "https://two.test/doom.zip" } ] }
            """);

        await host.Resolve<ICatalogImportService>().RunAsync(
            new ImportRunOptions { SourceKeys = [ScriptableCatalogSource.SourceKey] });

        var page = await host.Resolve<ICatalogListingRepository>()
            .QueryAsync(new CatalogListingQuery { Take = 50 });

        var listing = Assert.Single(page.Items);

        Assert.Equal(1993, listing.Year);

        // One card, both feeds named on it.
        Assert.Equal(["one", "two"], listing.SourceKeys.Order());

        // Both addresses kept. Mirrors are additive across sources, so the
        // second feed adds a fallback rather than replacing the first.
        var loaded = await host.Resolve<ICatalogListingRepository>().GetAsync(listing.ListingId);

        Assert.NotNull(loaded);
        Assert.True(loaded.IsDownloadable);

        Assert.Equal(
            ["https://one.test/doom.zip", "https://two.test/doom.zip"],
            loaded.Downloads.Select(download => download.Url).Order());
    }

    [Fact]
    public async Task A_feed_listing_pages_an_adapter_handles_is_offered_without_addresses()
    {
        // The point of a catalogue feed. It lists names and Archive item pages
        // and publishes no download link at all; the addresses are worked out at
        // install time by the built-in adapter. Marking these uninstallable hid
        // them behind Discover's "installable only" filter, which is on by
        // default — so a working feed looked like it had imported nothing.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;
        var settings = host.Resolve<ISettingsService>();

        await settings.SaveAsync(settings.Current with { DiscoveryEnabled = true });

        await File.WriteAllTextAsync(Path.Combine(directory, "bare.yaml"), CatalogManifest("bare"));
        await File.WriteAllTextAsync(Path.Combine(directory, "games.json"), Payload());

        await host.Resolve<ICatalogImportService>().RunAsync(
            new ImportRunOptions { SourceKeys = [ScriptableCatalogSource.SourceKey] });

        var repository = host.Resolve<ICatalogListingRepository>();

        var offered = await repository.QueryAsync(
            new CatalogListingQuery { Take = 50, DownloadableOnly = true });

        Assert.Equal(2, offered.Items.Count);
    }

    [Fact]
    public async Task A_feed_pointing_nowhere_recognisable_is_described_but_not_offered()
    {
        // The other side of the same rule. No source published a file and no
        // adapter claims the host, so nothing can be promised — the game is in
        // the catalogue and better described for it, but the launcher does not
        // pretend it can fetch something it has no way to reach.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;
        var settings = host.Resolve<ISettingsService>();

        await settings.SaveAsync(settings.Current with { DiscoveryEnabled = true });

        await File.WriteAllTextAsync(Path.Combine(directory, "bare.yaml"), CatalogManifest("bare"));

        await File.WriteAllTextAsync(
            Path.Combine(directory, "games.json"),
            """
            { "games": [
              { "id": "x", "title": "Obscure Thing", "year": 1994,
                "page": "https://nobody-handles-this.test/games/obscure" } ] }
            """);

        await host.Resolve<ICatalogImportService>().RunAsync(
            new ImportRunOptions { SourceKeys = [ScriptableCatalogSource.SourceKey] });

        var repository = host.Resolve<ICatalogListingRepository>();

        // Present in the catalogue...
        Assert.Single((await repository.QueryAsync(new CatalogListingQuery { Take = 50 })).Items);

        // ...but not offered for install.
        Assert.Empty(
            (await repository.QueryAsync(new CatalogListingQuery { Take = 50, DownloadableOnly = true })).Items);
    }

    /// <summary>Builds the source over a host's real manifest store and hooks.</summary>
    /// <param name="host">The container under test.</param>
    /// <param name="robotsAllow">What the robots policy should answer.</param>
    /// <returns>The source.</returns>
    /// <remarks>
    /// The robots policy is the one thing substituted: the real one would fetch
    /// <c>robots.txt</c> from a host that does not exist.
    /// </remarks>
    private static ScriptableCatalogSource Source(TestAppHost host, bool robotsAllow = true) =>
        new(
            host.Resolve<IFeedManifestStore>(),
            host.Resolve<IScriptHookRunner>(),
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            new FixedRobots(robotsAllow),
            new AlwaysDiscovering(),
            host.Resolve<IAppPaths>(),
            NullLogger<ScriptableCatalogSource>.Instance);

    /// <summary>Drains an enumeration expected to yield exactly one reference.</summary>
    /// <param name="source">The source to drain.</param>
    /// <returns>The single reference.</returns>
    private static async Task<SourceListingRef> SingleAsync(ScriptableCatalogSource source)
    {
        var references = new List<SourceListingRef>();

        await foreach (var reference in source.EnumerateAsync(new SourceEnumerationOptions()))
        {
            references.Add(reference);
        }

        return Assert.Single(references);
    }

    /// <summary>A manifest that reads a catalogue file beside itself.</summary>
    /// <param name="key">The manifest key.</param>
    /// <param name="file">The payload file it reads.</param>
    /// <returns>YAML text.</returns>
    private static string CatalogManifest(string key, string file = "games.json") =>
        $"""
         key: {key}
         displayName: {key} catalogue
         catalog:
           request:
             url: {file}
           format: json
           items: games
           map:
             title: title
             id: id
             year: year
             description: summary
             developer: developer
             page: page
             downloadUrl: download

         """;

    /// <summary>Two games, one of which names its page.</summary>
    /// <returns>JSON text.</returns>
    private static string Payload() =>
        """
        { "games": [
          { "id": "msdos_Doom_1993", "title": "Doom", "year": 1993,
            "summary": "A landmark shooter.", "developer": "id Software",
            "page": "https://archive.org/details/msdos_Doom_1993" },
          { "id": "pop", "title": "Prince of Persia", "year": 1989,
            "page": "https://archive.org/details/msdos_Prince_of_Persia_1990" } ] }
        """;

    /// <summary>A settings service with discovery switched on.</summary>
    private sealed class AlwaysDiscovering : ISettingsService
    {
        public AppSettings Current { get; private set; } = new() { DiscoveryEnabled = true };

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }

    /// <summary>A robots policy with a fixed answer, so tests never reach the network.</summary>
    private sealed class FixedRobots(bool allowed) : IRobotsPolicy
    {
        public Task<bool> IsAllowedAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);

        public Task<TimeSpan?> GetCrawlDelayAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(null);
    }
}
