using System.Net;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Install;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Desktop.Services.Discovery.Sourcing;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the sourcing adapters and the fallback that finds a download for a
/// listing whose own source cannot supply one.
/// </summary>
public sealed class SourcingAdapterTests
{
    [Theory]
    [InlineData("https://www.myabandonware.com/game/doom-1id", true)]
    [InlineData("https://myabandonware.com/game/doom-1id", true)]
    [InlineData("https://archive.org/details/msdos_Doom_1993", false)]
    [InlineData("not a url", false)]
    public void An_adapter_only_claims_its_own_site(string url, bool expected) =>
        Assert.Equal(expected, Adapter(allowDownloads: false).CanHandle(url));

    [Fact]
    public async Task MyAbandonware_refuses_because_its_own_rules_disallow_the_path()
    {
        // Checked against the live rules rather than hardcoded, so the refusal is
        // a fact about the site rather than an assumption baked into the code.
        var payload = await Adapter(allowDownloads: false).ExtractDownloadPayloadAsync(
            Listing("Doom"), "https://www.myabandonware.com/game/doom-1id");

        Assert.False(payload.HasDownloads);
        Assert.Equal(SourcingRefusal.DisallowedByRobots, payload.Refusal);
        Assert.Contains("does not permit automated downloads", payload.Explanation);
    }

    [Fact]
    public async Task A_refusal_is_distinguishable_from_a_site_being_unreachable()
    {
        // The distinction matters to the caller: an unreachable site is worth
        // retrying and a forbidden path never will be.
        var permitted = await Adapter(allowDownloads: true).ExtractDownloadPayloadAsync(
            Listing("Doom"), "https://www.myabandonware.com/game/doom-1id");

        Assert.Equal(SourcingRefusal.NoPayload, permitted.Refusal);
        Assert.NotEqual(SourcingRefusal.DisallowedByRobots, permitted.Refusal);
    }

    [Theory]
    [InlineData("https://archive.org/details/msdos_Doom_1993", true)]
    [InlineData("https://archive.org/download/msdos_Doom_1993/Doom_1993.zip", true)]
    [InlineData("https://archive.org/metadata/msdos_Doom_1993", true)]
    [InlineData("https://www.archive.org/details/msdos_Doom_1993", true)]
    [InlineData("https://archive.org/details/", false)]
    [InlineData("https://archive.org/", false)]
    [InlineData("https://www.myabandonware.com/game/doom-1id", false)]
    public void The_archive_adapter_claims_any_address_naming_an_item(string url, bool expected) =>
        Assert.Equal(expected, ArchiveAdapter(new StubHttpClientFactory()).CanHandle(url));

    [Fact]
    public async Task An_archive_item_yields_direct_addresses_their_mirrors_and_a_torrent()
    {
        // The gap this closes: an address that was never imported, or whose files
        // changed since it was, still resolves — the catalogue only knows what it
        // recorded when it last ran.
        var http = new StubHttpClientFactory()
            .Json("/metadata/msdos_Doom_1993", Fixture("archive-downloadable-item.json"));

        var payload = await ArchiveAdapter(http).ExtractDownloadPayloadAsync(
            Listing("Doom"), "https://archive.org/details/msdos_Doom_1993");

        Assert.True(payload.HasDownloads);

        var primary = payload.Downloads[0];

        Assert.Equal(
            "https://archive.org/download/msdos_Doom_1993/Doom_1993.zip", primary.Url);
        Assert.Equal("dddddddddddddddddddddddddddddddddddddddd", primary.Sha1);
        Assert.Equal("cccccccccccccccccccccccccccccccc", primary.Md5);
        Assert.Equal(2359527, primary.SizeBytes);

        // The node hosts the metadata names, as alternates rather than first:
        // faster, but they stop working if the Archive moves the item.
        Assert.Contains(payload.Downloads, download => download.Url.Contains("ia601403.us.archive.org"));
        Assert.Contains(payload.Downloads, download => download.Url.Contains("ia801403.us.archive.org"));

        // Last of all, because it needs aria2c and that may not be installed.
        Assert.Equal(DownloadKind.Torrent, payload.Downloads[^1].Kind);
        Assert.EndsWith("_archive.torrent", payload.Downloads[^1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_item_is_named_by_the_address_not_the_file_within_it()
    {
        var http = new StubHttpClientFactory()
            .Json("/metadata/msdos_Doom_1993", Fixture("archive-downloadable-item.json"));

        await ArchiveAdapter(http).ExtractDownloadPayloadAsync(
            Listing("Doom"), "https://archive.org/download/msdos_Doom_1993/Doom_1993.zip");

        // '/download/doom/doom.zip' names the item 'doom', not the file.
        Assert.Equal("https://archive.org/metadata/msdos_Doom_1993", Assert.Single(http.Requests));
    }

    [Fact]
    public async Task An_access_restricted_item_is_explained_rather_than_offered()
    {
        // Its addresses answer 403. Offering them would turn a clear explanation
        // into a failed download.
        var http = new StubHttpClientFactory()
            .Json("/metadata/", Fixture("archive-restricted-item.json"));

        var payload = await ArchiveAdapter(http).ExtractDownloadPayloadAsync(
            Listing("Restricted"), "https://archive.org/details/restricted_item");

        Assert.False(payload.HasDownloads);
        Assert.Equal(SourcingRefusal.NoPayload, payload.Refusal);
        Assert.Contains("viewed but not downloaded", payload.Explanation);
    }

    [Fact]
    public async Task An_item_the_archive_does_not_have_is_not_reported_as_unreachable()
    {
        // The distinction the caller acts on: a missing item never appears, an
        // unreachable site is worth trying again.
        var http = new StubHttpClientFactory().Status("/metadata/", HttpStatusCode.NotFound);

        var payload = await ArchiveAdapter(http).ExtractDownloadPayloadAsync(
            Listing("Nothing"), "https://archive.org/details/no_such_item");

        Assert.Equal(SourcingRefusal.NoPayload, payload.Refusal);
    }

    [Fact]
    public async Task A_listing_with_its_own_downloads_needs_no_resolution()
    {
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);
        listing.Downloads = [Download("https://a.test/doom.zip")];

        var payload = await Resolver(host).ResolveAsync(listing);

        Assert.True(payload.HasDownloads);
        Assert.Equal(SourcingRefusal.None, payload.Refusal);
    }

    [Fact]
    public async Task The_same_game_described_elsewhere_supplies_the_download()
    {
        // The whole point of keeping a metadata-only source: a game it describes
        // and the Archive also holds is installable through the Archive and
        // better described because of the other one.
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        var describedOnly = Listing("Doom", 1993, "lst_meta");
        var downloadable = Listing("Doom", 1993, "lst_archive");

        downloadable.Downloads = [Download("https://archive.test/doom.zip")];

        await repository.UpsertManyAsync([describedOnly, downloadable]);

        var payload = await Resolver(host).ResolveAsync(describedOnly);

        Assert.True(payload.HasDownloads);
        Assert.Equal("https://archive.test/doom.zip", payload.Downloads[0].Url);
    }

    [Fact]
    public async Task A_different_release_of_the_same_title_is_not_borrowed_from()
    {
        // Prince of Persia 1989 and 2008 share a title and are not the same
        // game. The fallback follows the same year rule the importer merges by.
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        var original = Listing("Prince of Persia", 1989, "lst_original");
        var remake = Listing("Prince of Persia", 2008, "lst_remake");

        remake.Downloads = [Download("https://archive.test/pop2008.zip")];

        await repository.UpsertManyAsync([original, remake]);

        var payload = await Resolver(host).ResolveAsync(original);

        Assert.False(payload.HasDownloads);
    }

    [Fact]
    public async Task A_listing_nothing_can_supply_reports_that_plainly()
    {
        using var host = new TestAppHost();

        var listing = Listing("Obscure Thing", 1994);
        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync([listing]);

        var payload = await Resolver(host).ResolveAsync(listing);

        Assert.False(payload.HasDownloads);

        // The resolver reports the kind of refusal; turning it into a sentence
        // is the install service's job, because only it knows whether the
        // listing was restricted or merely undescribed.
        Assert.Equal(SourcingRefusal.NoPayload, payload.Refusal);
    }

    [Fact]
    public async Task Every_adapters_addresses_are_merged_into_one_mirror_list()
    {
        // Not the first that answers. A download that dies halfway is ordinary
        // for these hosts, and the transfer only survives it if the alternate is
        // already on the row.
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);
        await SeedSourceAsync(host, listing, "https://archive.org/details/msdos_Doom_1993");

        var preferred = new StubAdapter(
            "user-feed",
            claims: true,
            new SourcingPayload([Download("https://feed.test/doom.zip")]),
            priority: 100);

        var builtIn = new StubAdapter(
            "built-in", claims: true, new SourcingPayload([Download("https://archive.test/doom.zip")]));

        var payload = await ResolverOver(host, preferred, builtIn).ResolveAsync(listing);

        Assert.True(builtIn.WasAsked);
        Assert.Equal(2, payload.Downloads.Count);

        Assert.Equal("https://feed.test/doom.zip", payload.Downloads[0].Url);
        Assert.Equal("https://archive.test/doom.zip", payload.Downloads[1].Url);
    }

    [Fact]
    public async Task Priority_decides_the_order_addresses_are_tried_in()
    {
        // Registration order deliberately disagrees with priority here, so a
        // result matching priority cannot be the registration order by accident.
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);
        await SeedSourceAsync(host, listing, "https://archive.org/details/msdos_Doom_1993");

        var lastResort = new StubAdapter(
            "slow-mirror",
            claims: true,
            new SourcingPayload([Download("https://slow.test/doom.zip")]),
            priority: -10);

        var builtIn = new StubAdapter(
            "built-in", claims: true, new SourcingPayload([Download("https://archive.test/doom.zip")]));

        var preferred = new StubAdapter(
            "user-feed",
            claims: true,
            new SourcingPayload([Download("https://feed.test/doom.zip")]),
            priority: 100);

        var payload = await ResolverOver(host, lastResort, builtIn, preferred).ResolveAsync(listing);

        Assert.Equal(
            ["https://feed.test/doom.zip", "https://archive.test/doom.zip", "https://slow.test/doom.zip"],
            payload.Downloads.Select(download => download.Url));

        // Renumbered across the merged list. Each adapter numbers its own rows
        // from zero, so leaving those alone would give every row rank 0.
        Assert.Equal([0, 1, 2], payload.Downloads.Select(download => download.MirrorRank));
    }

    [Fact]
    public async Task An_address_two_adapters_both_offer_is_only_tried_once()
    {
        // Two adapters describing one host routinely produce the same address.
        // Keeping both would have aria2c retry a URL that just failed and count
        // it as a fallback.
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);
        await SeedSourceAsync(host, listing, "https://archive.org/details/msdos_Doom_1993");

        var feed = new StubAdapter(
            "user-feed",
            claims: true,
            new SourcingPayload([Download("https://archive.test/doom.zip")]),
            priority: 100);

        var builtIn = new StubAdapter(
            "built-in",
            claims: true,
            new SourcingPayload([Download("https://archive.test/doom.zip"), Download("https://other.test/doom.zip")]));

        var payload = await ResolverOver(host, feed, builtIn).ResolveAsync(listing);

        Assert.Equal(
            ["https://archive.test/doom.zip", "https://other.test/doom.zip"],
            payload.Downloads.Select(download => download.Url));
    }

    [Fact]
    public async Task A_failing_adapter_does_not_take_the_other_mirrors_with_it()
    {
        // Task.WhenAll surfaces one exception and abandons the rest of the
        // results, so an unreachable host must never be allowed to throw out of
        // the concurrent gather.
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);
        await SeedSourceAsync(host, listing, "https://archive.org/details/msdos_Doom_1993");

        var builtIn = new StubAdapter(
            "built-in", claims: true, new SourcingPayload([Download("https://archive.test/doom.zip")]));

        var payload = await ResolverOver(host, new ThrowingAdapter(), builtIn).ResolveAsync(listing);

        Assert.Equal("https://archive.test/doom.zip", Assert.Single(payload.Downloads).Url);
    }

    [Fact]
    public async Task An_adapter_that_declines_does_not_stop_a_later_one_answering()
    {
        // The scriptable adapter answers CanHandle before it has read the
        // adapter folder, so it deliberately guesses yes. Registered first, a
        // wrong guess would otherwise take the address away from the built-in
        // that can actually supply it — an install that silently stopped
        // working after a restart.
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);
        await SeedSourceAsync(host, listing, "https://archive.org/details/msdos_Doom_1993");

        var guessed = new StubAdapter("user-feed", claims: true, SourcingPayload.Unsupported);

        var builtIn = new StubAdapter(
            "built-in", claims: true, new SourcingPayload([Download("https://archive.test/doom.zip")]));

        var payload = await ResolverOver(host, guessed, builtIn).ResolveAsync(listing);

        Assert.True(guessed.WasAsked);
        Assert.True(payload.HasDownloads);
        Assert.Equal("https://archive.test/doom.zip", payload.Downloads[0].Url);
    }

    [Fact]
    public async Task A_real_refusal_survives_a_later_adapter_merely_declining()
    {
        // "Their rules forbid this path" is worth telling someone. "I do not
        // handle this address" is not, and letting the second overwrite the
        // first would replace the only useful explanation with nothing.
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);
        await SeedSourceAsync(host, listing, "https://blocked.test/game/doom");

        var refused = new StubAdapter(
            "blocked",
            claims: true,
            new SourcingPayload([], SourcingRefusal.DisallowedByRobots, "blocked.test disallows that path."));

        var silent = new StubAdapter("other", claims: true, SourcingPayload.Unsupported);

        var payload = await ResolverOver(host, refused, silent).ResolveAsync(listing);

        Assert.False(payload.HasDownloads);
        Assert.Equal(SourcingRefusal.DisallowedByRobots, payload.Refusal);
        Assert.Contains("disallows", payload.Explanation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chosen_source_is_tried_first_without_losing_the_others()
    {
        // Reordered, not filtered. Choosing a source says which to try first,
        // not that the install should fail when that one is unreachable.
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);

        listing.Downloads =
        [
            Download("https://archive.test/doom.zip", "internet-archive", rank: 0),
            Download("https://feed.test/doom.zip", "my-feed", rank: 1)
        ];

        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync([listing]);

        var install = host.Resolve<IListingInstallService>();

        var mirrors = install.GetMirrors(listing, "my-feed");

        Assert.Equal(
            ["https://feed.test/doom.zip", "https://archive.test/doom.zip"],
            mirrors.Select(mirror => mirror.Url.AbsoluteUri));

        // Untouched without a preference: the catalogue's own ranking stands.
        Assert.Equal(
            "https://archive.test/doom.zip",
            install.GetMirrors(listing)[0].Url.AbsoluteUri);
    }

    [Fact]
    public async Task Preferring_a_source_does_not_promote_its_torrent_over_a_direct_address()
    {
        // A torrent needs aria2c, which may not be installed. Choosing a source
        // should not quietly make an install depend on it.
        using var host = new TestAppHost();

        var listing = Listing("Doom", 1993);

        var torrent = Download("https://feed.test/doom.torrent", "my-feed", rank: 0);
        torrent.Kind = DownloadKind.Torrent;

        listing.Downloads = [torrent, Download("https://feed.test/doom.zip", "my-feed", rank: 1)];

        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync([listing]);

        var mirrors = host.Resolve<IListingInstallService>().GetMirrors(listing, "my-feed");

        Assert.Equal("https://feed.test/doom.zip", mirrors[0].Url.AbsoluteUri);
        Assert.Equal(DownloadKind.Torrent, torrent.Kind);
    }

    [Fact]
    public async Task A_restricted_listings_own_addresses_are_never_used()
    {
        // The source said the item may be looked at but not taken away, so its
        // addresses answer 403. Using them would turn a clear explanation into
        // a failed download.
        using var host = new TestAppHost();

        var listing = Listing("Restricted Thing", 1990);
        listing.IsDownloadable = false;
        listing.Downloads = [Download("https://restricted.test/thing.zip")];

        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync([listing]);

        var payload = await Resolver(host).ResolveAsync(listing);

        Assert.False(payload.HasDownloads);
    }

    [Fact]
    public async Task Installing_falls_back_to_another_source_rather_than_giving_up()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        server.AddFile("doom.zip", Archive());

        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        var describedOnly = Listing("Doom", 1993, "lst_meta");
        var downloadable = Listing("Doom", 1993, "lst_archive");

        downloadable.Downloads = [Download(server.FileUrl("doom.zip").AbsoluteUri)];

        await repository.UpsertManyAsync([describedOnly, downloadable]);

        // Installing the listing that carries no download of its own still works.
        var result = await host.Resolve<IListingInstallService>().PrepareAsync("lst_meta");

        Assert.True(result.Succeeded);
        Assert.Equal("Doom", result.Listing.Title);
    }

    private static MyAbandonwareSourcingAdapter Adapter(bool allowDownloads) =>
        new(new FixedRobots(allowDownloads), NullLogger<MyAbandonwareSourcingAdapter>.Instance);

    private static InternetArchiveSourcingAdapter ArchiveAdapter(StubHttpClientFactory http) =>
        new(http, NullLogger<InternetArchiveSourcingAdapter>.Instance);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Discovery", "Fixtures", name));

    private static DownloadSourceResolver Resolver(TestAppHost host) =>
        new(
            [Adapter(allowDownloads: false)],
            host.Resolve<ICatalogListingRepository>(),
            host.Resolve<IListingNormalizer>(),
            NullLogger<DownloadSourceResolver>.Instance);

    private static CatalogListing Listing(string title, int? year = 1993, string id = "lst_1") => new()
    {
        ListingId = id,
        Title = title,
        SortTitle = TitleNormalizer.ToSortTitle(title),
        Year = year,
        MatchKey = TitleNormalizer.ComputeMatchKey(title, year),
        PrimarySourceKey = "test",
        ContentHash = id,
        IsDownloadable = true
    };

    private static ListingDownload Download(
        string url,
        string sourceKey = "internet-archive",
        int rank = 0) => new()
    {
        ListingId = "lst_archive",
        SourceKey = sourceKey,
        Url = url,
        FileName = "doom.zip",
        MirrorRank = rank
    };

    private static byte[] Archive()
    {
        using var buffer = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = archive.CreateEntry("DOOM.EXE").Open())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("MZ fake executable");
            stream.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Seeds the source observation the resolver reads before asking adapters.
    /// </summary>
    /// <param name="host">The container under test.</param>
    /// <param name="listing">The listing the observation belongs to.</param>
    /// <param name="url">The page address the adapters will be offered.</param>
    /// <returns>A task that completes once the row exists.</returns>
    /// <remarks>
    /// The resolver dispatches on the source's own recorded address rather than
    /// on anything the merged row carries, so a test about dispatch has to write
    /// a real observation.
    /// </remarks>
    private static async Task SeedSourceAsync(TestAppHost host, CatalogListing listing, string url)
    {
        var observation = new SourceListing
        {
            SourceKey = "test",
            SourceItemId = "item",
            SourceUrl = new Uri(url),
            Title = listing.Title,
            Year = listing.Year,
            RawPayload = "{}"
        };

        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([listing]);
        await repository.UpsertSourceAsync(new ListingSourceRecord
        {
            ListingId = listing.ListingId,
            SourceKey = observation.SourceKey,
            SourceItemId = observation.SourceItemId,
            SourceUrl = url,
            NormalizedJson = System.Text.Json.JsonSerializer.Serialize(
                observation,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            FetchedAt = DateTimeOffset.Now,
            SourceContentHash = "hash"
        });
    }

    /// <summary>Builds a resolver over an explicit list of adapters, in order.</summary>
    /// <param name="host">The container under test.</param>
    /// <param name="adapters">The adapters, highest precedence first.</param>
    /// <returns>The resolver.</returns>
    private static DownloadSourceResolver ResolverOver(TestAppHost host, params ISourcingAdapter[] adapters) =>
        new(
            adapters,
            host.Resolve<ICatalogListingRepository>(),
            host.Resolve<IListingNormalizer>(),
            NullLogger<DownloadSourceResolver>.Instance);

    /// <summary>An adapter with a fixed answer, for testing dispatch and ranking.</summary>
    /// <param name="key">Its dispatch key.</param>
    /// <param name="claims">Whether it claims every address.</param>
    /// <param name="answer">What it returns when asked.</param>
    /// <param name="priority">Where its addresses rank; zero is the built-in baseline.</param>
    private sealed class StubAdapter(
        string key,
        bool claims,
        SourcingPayload answer,
        int priority = 0) : ISourcingAdapter
    {
        /// <summary>Gets a value indicating whether this adapter was actually asked.</summary>
        public bool WasAsked { get; private set; }

        public string Key => key;

        public string DisplayName => key;

        public int Priority => priority;

        public bool CanHandle(string url) => claims;

        public Task<SourcingPayload> ExtractDownloadPayloadAsync(
            CatalogListing listing,
            string url,
            CancellationToken cancellationToken = default)
        {
            WasAsked = true;
            return Task.FromResult(answer);
        }
    }

    /// <summary>An adapter that fails, to prove one failing does not stop the rest.</summary>
    private sealed class ThrowingAdapter : ISourcingAdapter
    {
        public string Key => "throwing";

        public string DisplayName => "Throwing adapter";

        public bool CanHandle(string url) => true;

        public Task<SourcingPayload> ExtractDownloadPayloadAsync(
            CatalogListing listing,
            string url,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("this adapter is broken");
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
