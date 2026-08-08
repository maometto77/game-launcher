using System.Net;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Sources;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the Internet Archive source against payloads captured from the live
/// API, including the parts of its schema that are not consistently typed.
/// </summary>
public sealed class InternetArchiveSourceTests
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Discovery", "Fixtures");

    [Fact]
    public async Task A_downloadable_item_maps_onto_a_listing()
    {
        var (source, _) = Build().Json("/metadata/msdos_Doom_1993", Fixture("archive-downloadable-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Doom_1993"));

        Assert.NotNull(listing);
        Assert.Equal("Doom", listing.Title);
        Assert.Equal(1993, listing.Year);
        Assert.Equal("id Software, Inc.", listing.Developer);
        Assert.Equal("GT Interactive Software Corp.", listing.Publisher);
        Assert.Equal(["Action"], listing.Genres);
        Assert.Equal(["DOS"], listing.Platforms);
        Assert.True(listing.IsDownloadable);
        Assert.Equal(
            "https://archive.org/details/msdos_Doom_1993", listing.SourceUrl.AbsoluteUri);
    }

    [Fact]
    public async Task Per_file_checksums_reach_the_download_unchanged()
    {
        // The reason integrity verification costs nothing here: the existing
        // download path already infers the algorithm from the digest's length.
        var (source, _) = Build().Json("/metadata/", Fixture("archive-downloadable-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Doom_1993"));

        var primary = listing!.Downloads[0];

        Assert.Equal("dddddddddddddddddddddddddddddddddddddddd", primary.Sha1);
        Assert.Equal("cccccccccccccccccccccccccccccccc", primary.Md5);
        Assert.Equal(2359527, primary.SizeBytes);
        Assert.Equal("Doom_1993.zip", primary.FileName);

        // SHA-1 is preferred over MD5 when both are present.
        Assert.Equal(primary.Sha1, primary.BestChecksum);
    }

    [Fact]
    public async Task The_redirector_is_offered_first_and_the_direct_hosts_as_mirrors()
    {
        var (source, _) = Build().Json("/metadata/", Fixture("archive-downloadable-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Doom_1993"));

        var files = listing!.Downloads
            .Where(download => download.Kind == DownloadKind.Game)
            .Select(download => download.Url.AbsoluteUri)
            .ToArray();

        Assert.Equal(3, files.Length);

        // Rank 0 re-resolves to a working server on every request, so it cannot
        // go stale the way a recorded host can.
        Assert.StartsWith("https://archive.org/download/", files[0]);
        Assert.Contains("ia601403.us.archive.org", files[1]);
        Assert.Contains("ia801403.us.archive.org", files[2]);
        Assert.All(files, url => Assert.EndsWith("Doom_1993.zip", url));
    }

    [Fact]
    public async Task The_items_own_torrent_is_offered_after_the_direct_addresses()
    {
        // The Archive generates a torrent for most items and asks that large
        // transfers use it, because peers carry the load instead of its servers.
        var (source, _) = Build().Json("/metadata/", Fixture("archive-downloadable-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Doom_1993"));

        var torrent = listing!.Downloads.Single(download => download.Kind == DownloadKind.Torrent);

        Assert.EndsWith("msdos_Doom_1993_archive.torrent", torrent.Url.AbsoluteUri);

        // Last, because it needs an engine that may not be installed. A direct
        // address always works and must be what an install reaches for first.
        Assert.Same(torrent, listing.Downloads[^1]);

        // Its size is the size of the .torrent file, not of what it delivers, so
        // reporting it as the download size would be misleading.
        Assert.Null(torrent.SizeBytes);
    }

    [Fact]
    public async Task An_item_that_opts_out_of_torrents_is_respected()
    {
        // noarchivetorrent is the Archive saying this item has none. The
        // restricted fixture carries the flag.
        var (source, _) = Build().Json("/metadata/", Fixture("archive-restricted-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Oregon_Trail_The_1990"));

        Assert.DoesNotContain(listing!.Downloads, download => download.Kind == DownloadKind.Torrent);
    }

    [Fact]
    public async Task A_restricted_item_is_listed_but_never_offered_for_install()
    {
        // Real data: this item carries access-restricted-item and sits in
        // stream_only. Offering it would produce a 403 on every attempt.
        var (source, _) = Build().Json("/metadata/", Fixture("archive-restricted-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Oregon_Trail_The_1990"));

        Assert.NotNull(listing);
        Assert.Equal("Oregon Trail, The", listing.Title);
        Assert.False(listing.IsDownloadable);
        Assert.Empty(listing.Downloads);
    }

    [Fact]
    public async Task A_field_that_is_sometimes_a_string_and_sometimes_an_array_reads_either_way()
    {
        // collection is an array on one fixture and a bare string on the other;
        // subject is the reverse. Both are real shapes from the same API.
        var (arrayShaped, _) = Build().Json("/metadata/", Fixture("archive-restricted-item.json"));
        var (stringShaped, _) = Build().Json("/metadata/", Fixture("archive-downloadable-item.json"));

        var fromArray = await arrayShaped.FetchAsync(Reference("a"));
        var fromString = await stringShaped.FetchAsync(Reference("b"));

        Assert.Equal(["DOS"], fromArray!.Platforms);
        Assert.Equal(["DOS"], fromString!.Platforms);
        Assert.Equal(["education", "simulation"], fromArray.Tags);
        Assert.Equal(["shooter"], fromString.Tags);
    }

    [Fact]
    public async Task A_comma_separated_genre_field_becomes_several_genres()
    {
        var (source, _) = Build().Json("/metadata/", Fixture("archive-restricted-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Oregon_Trail_The_1990"));

        Assert.Equal(["Educational", "Simulation"], listing!.Genres);
    }

    [Fact]
    public async Task The_thumbnail_service_supplies_the_cover_and_originals_the_screenshots()
    {
        var (source, _) = Build().Json("/metadata/", Fixture("archive-downloadable-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Doom_1993"));

        var cover = listing!.Images.Single(image => image.Kind == ListingImageKind.Cover);

        Assert.Equal("https://archive.org/services/img/msdos_Doom_1993", cover.Url.AbsoluteUri);

        var screenshots = listing.Images.Where(image => image.Kind == ListingImageKind.Screenshot).ToArray();

        // The metadata XML and the Archive's own tile are both excluded.
        Assert.Single(screenshots);
        Assert.EndsWith("doom_screenshot.png", screenshots[0].Url.AbsoluteUri);
    }

    [Fact]
    public async Task Metadata_files_are_never_offered_as_downloads()
    {
        var (source, _) = Build().Json("/metadata/", Fixture("archive-downloadable-item.json"));

        var listing = await source.FetchAsync(Reference("msdos_Doom_1993"));

        Assert.DoesNotContain(
            listing!.Downloads, download => download.FileName!.EndsWith(".xml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_that_no_longer_exists_is_a_skip_rather_than_a_failure()
    {
        var (source, _) = Build().Status("/metadata/", HttpStatusCode.NotFound);

        Assert.Null(await source.FetchAsync(Reference("gone")));
    }

    [Fact]
    public async Task A_non_software_item_is_skipped()
    {
        var (source, _) = Build().Json(
            "/metadata/",
            """{"metadata":{"identifier":"a-book","mediatype":"texts","title":"A Book"}}""");

        Assert.Null(await source.FetchAsync(Reference("a-book")));
    }

    [Fact]
    public async Task A_server_error_is_raised_so_the_pipeline_can_retry_it()
    {
        // Distinct from "not found": a transport failure is transient and must
        // not be recorded as a permanent skip.
        var (source, _) = Build().Status("/metadata/", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            source.FetchAsync(Reference("boom")));
    }

    [Fact]
    public async Task Enumeration_follows_the_cursor_to_the_end()
    {
        // The scrape API signals the end with an empty cursor, not an empty page.
        var stub = new PagingStub(
        [
            """{"items":[{"identifier":"a","title":"A","item_last_updated":100}],"cursor":"c1","total":2}""",
            """{"items":[{"identifier":"b","title":"B","item_last_updated":200}],"cursor":"","total":2}"""
        ]);

        var collected = new List<SourceListingRef>();

        await foreach (var reference in Source(stub, out _).EnumerateAsync(new SourceEnumerationOptions()))
        {
            collected.Add(reference);
        }

        Assert.Equal(2, collected.Count);
        Assert.Equal("a", collected[0].SourceItemId);
        Assert.Equal("b", collected[1].SourceItemId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(100), collected[0].SourceUpdatedAt);
    }

    [Fact]
    public async Task The_cursor_carried_on_an_item_replays_its_page_rather_than_skipping_it()
    {
        var stub = new PagingStub(
        [
            """{"items":[{"identifier":"a"},{"identifier":"b"}],"cursor":"page-2","total":4}""",
            """{"items":[{"identifier":"c"},{"identifier":"d"}],"cursor":"","total":4}"""
        ]);

        var references = new List<SourceListingRef>();

        await foreach (var reference in Source(stub, out _).EnumerateAsync(new SourceEnumerationOptions()))
        {
            references.Add(reference);
        }

        // Items from the first page carry no cursor, so resuming starts over.
        // Items from the second carry the cursor that produced it, so resuming
        // replays that page — never one page too far, which would silently drop
        // up to a hundred items.
        Assert.Null(references[0].Cursor);
        Assert.Null(references[1].Cursor);
        Assert.Equal("page-2", references[2].Cursor);
        Assert.Equal("page-2", references[3].Cursor);
    }

    [Fact]
    public async Task The_query_is_fielded_and_pages_at_the_minimum_the_api_accepts()
    {
        var stub = new PagingStub(["""{"items":[],"cursor":"","total":0}"""]);
        var source = Source(stub, out _);

        await foreach (var _ in source.EnumerateAsync(new SourceEnumerationOptions()))
        {
            // Draining the enumeration is what issues the request.
        }

        var request = Uri.UnescapeDataString(stub.Requests[0]);

        // A bare free-text query is rejected with a 400, and count below 100 is
        // rejected outright. Both were established against the live API.
        Assert.Contains("collection:\"softwarelibrary_msdos_games\"", request);
        Assert.Contains("mediatype:software", request);
        Assert.Contains("count=100", request);
    }

    [Fact]
    public async Task Discovery_is_switched_off_until_the_user_asks_for_it()
    {
        // The default matters: a launcher that started crawling a third-party
        // service on first run would be taking a decision that is not its own.
        var settings = new FixedSettings();

        Assert.False(settings.Current.DiscoveryEnabled);

        var source = new InternetArchiveCatalogSource(
            new StubHttpClientFactory(), settings, NullLogger<InternetArchiveCatalogSource>.Instance);

        Assert.False(source.IsAvailable);

        await settings.SaveAsync(settings.Current with { DiscoveryEnabled = true });
        Assert.True(source.IsAvailable);
    }

    [Fact]
    public async Task An_item_whose_title_is_an_array_does_not_discard_the_whole_page()
    {
        // Found running against a real uploader's items: the search index returns
        // title as an array whenever an item carries more than one. Typed as a
        // plain string it throws, and because a page is deserialised in one pass
        // that single item silently discarded all hundred results beside it.
        var stub = new PagingStub(
        [
            """
            {"items":[
              {"identifier":"a","title":"Plain Title"},
              {"identifier":"b","title":["First Title","Second Title"]},
              {"identifier":"c","title":null},
              {"identifier":"d"}
            ],"cursor":"","total":4}
            """
        ]);

        var references = new List<SourceListingRef>();

        await foreach (var reference in Source(stub, out _).EnumerateAsync(new SourceEnumerationOptions()))
        {
            references.Add(reference);
        }

        Assert.Equal(4, references.Count);
        Assert.Equal("Plain Title", references[0].Title);

        // The first entry is what the Archive means by the primary value.
        Assert.Equal("First Title", references[1].Title);

        // A missing title falls back to the identifier rather than being dropped.
        Assert.Equal("c", references[2].Title);
        Assert.Equal("d", references[3].Title);
    }

    [Fact]
    public async Task An_uploader_can_be_imported_alongside_the_collections()
    {
        var settings = new FixedSettings();

        await settings.SaveAsync(settings.Current with
        {
            DiscoveryEnabled = true,
            InternetArchiveCollections = ["softwarelibrary_msdos_games"],
            InternetArchiveUploader = "someone@example.test"
        });

        var stub = new PagingStub(["""{"items":[],"cursor":"","total":0}"""]);

        var source = new InternetArchiveCatalogSource(
            stub, settings, NullLogger<InternetArchiveCatalogSource>.Instance);

        await foreach (var _ in source.EnumerateAsync(new SourceEnumerationOptions()))
        {
            // Draining the enumeration is what issues the request.
        }

        var request = Uri.UnescapeDataString(stub.Requests[0]);

        // Combined, not replaced: one pass can cover a curated library and one
        // person's uploads. The uploader field holds an email address, not a
        // screen name — a screen name matches nothing.
        Assert.Contains("collection:\"softwarelibrary_msdos_games\"", request);
        Assert.Contains("uploader:\"someone@example.test\"", request);
        Assert.Contains(" OR ", request);
        Assert.Contains("mediatype:software", request);
    }

    [Fact]
    public async Task An_uploader_alone_is_enough_to_make_the_source_available()
    {
        var settings = new FixedSettings();

        await settings.SaveAsync(settings.Current with
        {
            DiscoveryEnabled = true,
            InternetArchiveCollections = [],
            InternetArchiveUploader = "someone@example.test"
        });

        var source = new InternetArchiveCatalogSource(
            new StubHttpClientFactory(), settings, NullLogger<InternetArchiveCatalogSource>.Instance);

        Assert.True(source.IsAvailable);
    }

    [Fact]
    public async Task A_blank_uploader_is_treated_as_absent()
    {
        var settings = new FixedSettings();

        await settings.SaveAsync(settings.Current with
        {
            DiscoveryEnabled = true,
            InternetArchiveCollections = [],
            InternetArchiveUploader = "   "
        });

        var source = new InternetArchiveCatalogSource(
            new StubHttpClientFactory(), settings, NullLogger<InternetArchiveCatalogSource>.Instance);

        // Whitespace in a settings box must not produce uploader:"" — a fielded
        // query the scrape API rejects outright with a 400.
        Assert.False(source.IsAvailable);
    }

    [Fact]
    public async Task A_source_with_no_collections_or_uploader_reports_itself_unavailable()
    {
        var settings = new FixedSettings();

        await settings.SaveAsync(settings.Current with
        {
            DiscoveryEnabled = true,
            InternetArchiveCollections = [],
            InternetArchiveUploader = null
        });

        var source = new InternetArchiveCatalogSource(
            new StubHttpClientFactory(), settings, NullLogger<InternetArchiveCatalogSource>.Instance);

        Assert.False(source.IsAvailable);

        // Unavailable is not an error, and enumerating simply yields nothing.
        await foreach (var _ in source.EnumerateAsync(new SourceEnumerationOptions()))
        {
            Assert.Fail("an unconfigured source must not enumerate anything");
        }
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDirectory, name));

    private static SourceListingRef Reference(string identifier) =>
        new(InternetArchiveCatalogSource.SourceKey, identifier, identifier, null, null);

    private static FixtureBuilder Build() => new();

    private static InternetArchiveCatalogSource Source(
        System.Net.Http.IHttpClientFactory factory,
        out ISettingsService settings)
    {
        settings = new FixedSettings();

        return new InternetArchiveCatalogSource(
            factory, settings, NullLogger<InternetArchiveCatalogSource>.Instance);
    }

    /// <summary>Builds a source over a stub factory in one expression.</summary>
    private sealed class FixtureBuilder
    {
        private readonly StubHttpClientFactory _factory = new();

        public (InternetArchiveCatalogSource Source, StubHttpClientFactory Factory) Json(
            string fragment,
            string json)
        {
            _factory.Json(fragment, json);
            return (Source(_factory, out _), _factory);
        }

        public (InternetArchiveCatalogSource Source, StubHttpClientFactory Factory) Status(
            string fragment,
            HttpStatusCode status)
        {
            _factory.Status(fragment, status);
            return (Source(_factory, out _), _factory);
        }
    }

    /// <summary>Returns a different canned page on each call, as pagination does.</summary>
    private sealed class PagingStub(string[] pages) : System.Net.Http.IHttpClientFactory
    {
        private int _index;

        public List<string> Requests { get; } = [];

        public System.Net.Http.HttpClient CreateClient(string name) =>
            new(new Handler(this)) { Timeout = TimeSpan.FromSeconds(5) };

        private System.Net.Http.HttpResponseMessage Next(string url)
        {
            Requests.Add(url);

            var body = _index < pages.Length ? pages[_index++] : """{"items":[],"cursor":""}""";

            return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    body, System.Text.Encoding.UTF8, "application/json")
            };
        }

        private sealed class Handler(PagingStub owner) : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(owner.Next(request.RequestUri!.ToString()));
        }
    }

    /// <summary>Settings fixed at their defaults, with no file behind them.</summary>
    private sealed class FixedSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();

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
}
