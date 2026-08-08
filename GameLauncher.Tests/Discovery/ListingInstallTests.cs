using System.IO.Compression;
using System.Text;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Install;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers installing a catalogue listing through the existing download path,
/// including mirror failover over a real socket.
/// </summary>
public sealed class ListingInstallTests
{
    [Fact]
    public async Task A_listing_is_downloaded_unpacked_and_added_to_the_library()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        server.AddFile("doom.zip", Archive(("DOOM.EXE", "MZ fake executable")));

        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993, server.FileUrl("doom.zip").AbsoluteUri)]);

        var service = host.Resolve<IListingInstallService>();
        var prepared = await service.PrepareAsync("lst_1");

        Assert.True(prepared.Succeeded);
        Assert.Equal(1, prepared.MirrorsTried);
        Assert.True(prepared.Preparation!.WasArchive);
        Assert.NotEmpty(prepared.Preparation.Candidates);

        var candidate = prepared.Preparation.Candidates[0];

        var game = await service.CompleteAsync(
            prepared.Listing, candidate.ExecutablePath, prepared.Preparation.InstallDirectory);

        Assert.NotNull(game);
        Assert.Equal("Doom", game.Title);

        // The one link between the two subsystems.
        Assert.Equal("lst_1", game.ListingId);

        // Catalog identity is still minted by the ordinary import path, from the
        // executable that is now on disk — discovery plays no part in it.
        Assert.NotNull(game.CatalogId);

        var reloaded = await host.Resolve<IGameRepository>().GetByIdAsync(game.Id);

        Assert.Equal("lst_1", reloaded!.ListingId);
    }

    [Fact]
    public async Task A_failing_mirror_falls_through_to_the_next()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        server.AddFile("doom.zip", Archive(("DOOM.EXE", "MZ fake executable")));

        using var host = new TestAppHost();

        // The first address does not exist; the second does. Mirrors are what
        // make a single unreachable host a delay rather than a dead end.
        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync(
        [
            Listing("lst_1", "Doom", 1993,
                new Uri(server.BaseAddress, "/missing/doom.zip").AbsoluteUri,
                server.FileUrl("doom.zip").AbsoluteUri)
        ]);

        var result = await host.Resolve<IListingInstallService>().PrepareAsync("lst_1");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.MirrorsTried);
    }

    [Fact]
    public async Task Every_mirror_failing_is_reported_rather_than_thrown()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        using var host = new TestAppHost();

        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync(
        [
            Listing("lst_1", "Doom", 1993,
                new Uri(server.BaseAddress, "/missing/a.zip").AbsoluteUri,
                new Uri(server.BaseAddress, "/missing/b.zip").AbsoluteUri)
        ]);

        var result = await host.Resolve<IListingInstallService>().PrepareAsync("lst_1");

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.MirrorsTried);
        Assert.Contains("2 addresses", result.Message);
    }

    [Fact]
    public async Task A_checksum_that_does_not_match_moves_on_to_the_next_mirror()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        var archive = Archive(("DOOM.EXE", "MZ fake executable"));

        server.AddFile("bad.zip", archive);
        server.AddFile("good.zip", archive);

        using var host = new TestAppHost();

        var listing = Listing("lst_1", "Doom", 1993, server.FileUrl("bad.zip").AbsoluteUri);

        // A wrong digest on the first mirror, none on the second. A corrupt copy
        // on one host says nothing about another's.
        listing.Downloads[0].Sha1 = new string('0', 40);

        listing.Downloads =
        [
            listing.Downloads[0],
            new ListingDownload
            {
                ListingId = "lst_1",
                SourceKey = "test",
                Url = server.FileUrl("good.zip").AbsoluteUri,
                MirrorRank = 1
            }
        ];

        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync([listing]);

        var result = await host.Resolve<IListingInstallService>().PrepareAsync("lst_1");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.MirrorsTried);
    }

    [Fact]
    public async Task A_restricted_listing_explains_itself_instead_of_failing()
    {
        using var host = new TestAppHost();

        var listing = Listing("lst_1", "Oregon Trail", 1990, "https://example.test/x.zip");
        listing.IsDownloadable = false;

        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync([listing]);

        var result = await host.Resolve<IListingInstallService>().PrepareAsync("lst_1");

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.MirrorsTried);
        Assert.Contains("does not allow downloading", result.Message);
    }

    [Fact]
    public async Task Mirrors_are_offered_in_rank_order_and_non_http_addresses_are_ignored()
    {
        using var host = new TestAppHost();

        var listing = Listing("lst_1", "Doom", 1993, "https://b.test/second.zip");

        listing.Downloads =
        [
            new ListingDownload { ListingId = "lst_1", Url = "https://b.test/second.zip", MirrorRank = 5 },
            new ListingDownload { ListingId = "lst_1", Url = "https://a.test/first.zip", MirrorRank = 1 },

            // Not a transfer this launcher performs, and deliberately never has.
            new ListingDownload { ListingId = "lst_1", Url = "magnet:?xt=urn:btih:abc", MirrorRank = 0 }
        ];

        await host.Resolve<ICatalogListingRepository>().UpsertManyAsync([listing]);

        var stored = await host.Resolve<ICatalogListingRepository>().GetAsync("lst_1");
        var mirrors = host.Resolve<IListingInstallService>().GetMirrors(stored!);

        Assert.Equal(2, mirrors.Count);
        Assert.Equal("https://a.test/first.zip", mirrors[0].Url.AbsoluteUri);
        Assert.Equal("https://b.test/second.zip", mirrors[1].Url.AbsoluteUri);
    }

    [Fact]
    public async Task An_unknown_listing_is_rejected()
    {
        using var host = new TestAppHost();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Resolve<IListingInstallService>().PrepareAsync("lst_missing"));
    }

    private static CatalogListing Listing(string id, string title, int? year, params string[] urls)
    {
        var listing = new CatalogListing
        {
            ListingId = id,
            Title = title,
            SortTitle = TitleNormalizer.ToSortTitle(title),
            Year = year,
            MatchKey = TitleNormalizer.ComputeMatchKey(title, year),
            PrimarySourceKey = "test",
            ContentHash = Guid.NewGuid().ToString("N"),
            IsDownloadable = true,
            Genres = ["Action"]
        };

        listing.Downloads = urls
            .Select((url, index) => new ListingDownload
            {
                ListingId = id,
                SourceKey = "test",
                Url = url,
                FileName = Path.GetFileName(new Uri(url).AbsolutePath),
                MirrorRank = index
            })
            .ToArray();

        return listing;
    }

    /// <summary>Builds a zip in memory so the test needs no file on disk.</summary>
    private static byte[] Archive(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        return buffer.ToArray();
    }
}
