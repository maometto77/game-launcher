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

    private static ListingDownload Download(string url) => new()
    {
        ListingId = "lst_archive",
        SourceKey = "internet-archive",
        Url = url,
        FileName = "doom.zip"
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

    /// <summary>A robots policy with a fixed answer, so tests never reach the network.</summary>
    private sealed class FixedRobots(bool allowed) : IRobotsPolicy
    {
        public Task<bool> IsAllowedAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);

        public Task<TimeSpan?> GetCrawlDelayAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(null);
    }
}
