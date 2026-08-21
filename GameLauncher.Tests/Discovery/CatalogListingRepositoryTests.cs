using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers schema v7: normalised lookups, cascades, full-text search and the
/// batched write path.
/// </summary>
public sealed class CatalogListingRepositoryTests
{
    [Fact]
    public async Task A_listing_round_trips_with_every_collection()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993, listing =>
        {
            listing.Developer = "id Software";
            listing.Publisher = "GT Interactive";
            listing.Genres = ["Shooter", "Action"];
            listing.Platforms = ["DOS"];
            listing.Tags = ["classic"];
            listing.Downloads = [Download("https://a.test/doom.zip", "abc123")];
            listing.Images = [Image("https://a.test/cover.png", ListingImageKind.Cover)];
        })]);

        var loaded = await repository.GetAsync("lst_1");

        Assert.NotNull(loaded);
        Assert.Equal("Doom", loaded.Title);
        Assert.Equal(1993, loaded.Year);
        Assert.Equal("id Software", loaded.Developer);
        Assert.Equal("GT Interactive", loaded.Publisher);
        Assert.Equal(["Action", "Shooter"], loaded.Genres.OrderBy(genre => genre));
        Assert.Equal(["DOS"], loaded.Platforms);
        Assert.Equal(["classic"], loaded.Tags);
        Assert.Single(loaded.Downloads);
        Assert.Equal("abc123", loaded.Downloads[0].Sha1);
        Assert.Single(loaded.Images);
    }

    [Fact]
    public async Task A_sha256_survives_the_round_trip_and_wins_over_the_weaker_digests()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        await repository.UpsertManyAsync([Listing("lst_h", "Quake", 1996, listing =>
        {
            listing.Downloads =
            [
                new ListingDownload
                {
                    Url = "https://a.test/quake.zip",
                    SourceKey = "test",
                    Md5 = "cccccccccccccccccccccccccccccccc",
                    Sha1 = "dddddddddddddddddddddddddddddddddddddddd",
                    Sha256 = Sha256
                }
            ];
        })]);

        var loaded = await repository.GetAsync("lst_h");

        // The column arrived in a migration, so this is as much a check that the
        // schema moved as that the mapping is right: a missing column would fail
        // the insert, and a column missing from either SQL statement would come
        // back null having been written.
        var download = Assert.Single(loaded!.Downloads);

        Assert.Equal(Sha256, download.Sha256);

        // What the whole point of a self-hosted feed rests on: the strongest
        // digest is the one the download path is handed, and it infers SHA-256
        // from the length.
        Assert.Equal(Sha256, download.BestChecksum);
    }

    [Fact]
    public async Task Lookup_entities_are_shared_rather_than_duplicated()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        // The whole point of normalising: three spellings of one company become
        // one facet, not three that each filter part of the catalogue.
        await repository.UpsertManyAsync(
        [
            Listing("lst_1", "A", 1990, listing => listing.Developer = "MicroProse Software"),
            Listing("lst_2", "B", 1991, listing => listing.Developer = "MicroProse Software, Inc."),
            Listing("lst_3", "C", 1992, listing => listing.Developer = "MICROPROSE SOFTWARE"),

            // Kept separate on purpose. Folding a descriptive word away would
            // merge companies that merely share a first word.
            Listing("lst_4", "D", 1993, listing => listing.Developer = "MicroProse")
        ]);

        var facets = await repository.GetFacetsAsync();

        Assert.Equal(2, facets.Developers.Count);
        Assert.Equal(3, facets.Developers[0].Count);
        Assert.Equal("MicroProse Software", facets.Developers[0].Name);
    }

    [Fact]
    public async Task Genre_facets_count_listings()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync(
        [
            Listing("lst_1", "A", 1990, listing => listing.Genres = ["Action", "Shooter"]),
            Listing("lst_2", "B", 1991, listing => listing.Genres = ["Action"])
        ]);

        var facets = await repository.GetFacetsAsync();

        Assert.Equal("Action", facets.Genres[0].Name);
        Assert.Equal(2, facets.Genres[0].Count);
        Assert.Equal(1, facets.Genres.Single(facet => facet.Name == "Shooter").Count);
    }

    [Fact]
    public async Task Full_text_search_finds_a_listing_by_title_developer_and_genre()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync(
        [
            Listing("lst_1", "Doom", 1993, listing =>
            {
                listing.Developer = "id Software";
                listing.Genres = ["Shooter"];
                listing.Description = "A landmark first-person shooter.";
            }),
            Listing("lst_2", "SimCity", 1989, listing => listing.Developer = "Maxis")
        ]);

        Assert.Equal("lst_1", (await Search(repository, "doom")).Items.Single().ListingId);
        Assert.Equal("lst_1", (await Search(repository, "id Software")).Items.Single().ListingId);
        Assert.Equal("lst_1", (await Search(repository, "landmark")).Items.Single().ListingId);
        Assert.Equal("lst_2", (await Search(repository, "maxis")).Items.Single().ListingId);
    }

    [Fact]
    public async Task Search_matches_a_prefix_so_type_ahead_works()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Civilization", 1991)]);

        Assert.Single((await Search(repository, "civ")).Items);
        Assert.Single((await Search(repository, "civiliz")).Items);
    }

    [Fact]
    public async Task Search_ignores_diacritics()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Pokémon Trading Card Game", 1998)]);

        Assert.Single((await Search(repository, "pokemon")).Items);
    }

    [Fact]
    public async Task Search_syntax_in_user_input_is_a_literal_search_not_an_error()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993)]);

        // Raw FTS5 syntax would make each of these a query error rather than a
        // search, which is a crash on an ordinary keystroke.
        foreach (var input in new[] { "\"", "doom NOT", "^doom", "doom*(", "AND", "  " })
        {
            var page = await Search(repository, input);
            Assert.True(page.TotalCount >= 0);
        }
    }

    [Fact]
    public async Task The_search_index_follows_an_update()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Working Title", 1993)]);
        Assert.Single((await Search(repository, "working")).Items);

        await repository.UpsertManyAsync([Listing("lst_1", "Final Title", 1993)]);

        Assert.Empty((await Search(repository, "working")).Items);
        Assert.Single((await Search(repository, "final")).Items);
    }

    [Fact]
    public async Task An_unchanged_listing_is_not_rewritten()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        var listing = Listing("lst_1", "Doom", 1993);

        Assert.Equal(1, await repository.UpsertManyAsync([listing]));

        // Same content hash, so a second pass has nothing to do. This is what
        // makes an incremental import nearly free.
        Assert.Equal(0, await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993)]));
    }

    [Fact]
    public async Task A_cached_cover_survives_a_metadata_refresh()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993, listing =>
        {
            listing.CoverImageUrl = "https://a.test/cover.png";
            listing.Images = [Image("https://a.test/cover.png", ListingImageKind.Cover)];
        })]);

        await repository.SetImagePathAsync("lst_1", "https://a.test/cover.png", @"C:\cache\cover.png");

        // Refreshing the description must not throw away artwork already fetched.
        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993, listing =>
        {
            listing.Description = "Now with a description.";
            listing.CoverImageUrl = "https://a.test/cover.png";
            listing.Images = [Image("https://a.test/cover.png", ListingImageKind.Cover)];
        })]);

        var reloaded = await repository.GetAsync("lst_1");

        Assert.Equal(@"C:\cache\cover.png", reloaded!.CoverImagePath);
        Assert.Equal(@"C:\cache\cover.png", reloaded.Images[0].LocalPath);
    }

    [Fact]
    public async Task A_user_correction_survives_a_re_import()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993, listing =>
        {
            listing.UserOverride = """{"Title":"DOOM (1993)"}""";
            listing.IsHidden = true;
        })]);

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993, listing =>
            listing.Description = "Refreshed.")]);

        var reloaded = await repository.GetAsync("lst_1");

        Assert.Equal("""{"Title":"DOOM (1993)"}""", reloaded!.UserOverride);
        Assert.True(reloaded.IsHidden);
        Assert.Equal("Refreshed.", reloaded.Description);
    }

    [Fact]
    public async Task Matching_candidates_are_found_by_title_regardless_of_year()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync(
        [
            Listing("lst_1", "Prince of Persia", 1989),
            Listing("lst_2", "Prince of Persia", 2008),
            Listing("lst_3", "Doom", 1993)
        ]);

        var candidates = await repository.FindByTitleKeyAsync(
            TitleNormalizer.ComputeTitleKey("Prince of Persia"));

        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public async Task Filters_and_paging_narrow_the_catalogue()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync(
        [
            Listing("lst_1", "Alpha", 1990, listing => listing.Genres = ["Action"]),
            Listing("lst_2", "Beta", 1995, listing => listing.Genres = ["Puzzle"]),
            Listing("lst_3", "Gamma", 2000, listing => listing.Genres = ["Action"])
        ]);

        Assert.Equal(2, (await repository.QueryAsync(new CatalogListingQuery { Genre = "Action" })).TotalCount);
        Assert.Equal(2, (await repository.QueryAsync(new CatalogListingQuery { YearFrom = 1995 })).TotalCount);

        var page = await repository.QueryAsync(new CatalogListingQuery
        {
            Sort = CatalogListingSort.Title,
            Skip = 1,
            Take = 1
        });

        Assert.Equal(3, page.TotalCount);
        Assert.Equal("Beta", page.Items.Single().Title);
    }

    [Fact]
    public async Task A_hidden_listing_is_excluded_unless_asked_for()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993, listing => listing.IsHidden = true)]);

        Assert.Equal(0, (await repository.QueryAsync(new CatalogListingQuery())).TotalCount);
        Assert.Equal(1, (await repository.QueryAsync(new CatalogListingQuery { IncludeHidden = true })).TotalCount);
    }

    [Fact]
    public async Task Source_observations_round_trip_and_drive_a_re_merge()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993)]);

        await repository.UpsertSourceAsync(new ListingSourceRecord
        {
            ListingId = "lst_1",
            SourceKey = "test",
            SourceItemId = "doom",
            SourceUrl = "https://a.test/doom",
            NormalizedJson = System.Text.Json.JsonSerializer.Serialize(
                NormalizationTests.Build("Doom") with { Year = 1993 },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            FetchedAt = DateTimeOffset.Now,
            SourceContentHash = "hash-1"
        });

        var stored = await repository.GetSourceAsync("test", "doom");

        Assert.NotNull(stored);
        Assert.Equal("hash-1", stored.SourceContentHash);

        var observations = await repository.GetSourceListingsAsync("lst_1");

        Assert.Single(observations);
        Assert.Equal(1993, observations[0].Year);

        Assert.Equal(["lst_1"], await repository.GetListingIdsWithSourcesAsync("test"));
    }

    [Fact]
    public async Task Deleting_a_listing_takes_everything_hanging_off_it()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();
        var factory = host.Resolve<IDbConnectionFactory>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993, listing =>
        {
            listing.Genres = ["Action"];
            listing.Downloads = [Download("https://a.test/doom.zip")];
            listing.Images = [Image("https://a.test/cover.png", ListingImageKind.Cover)];
        })]);

        await using (var connection = await factory.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection, "DELETE FROM CatalogListing WHERE ListingId = 'lst_1';");

            Assert.Equal(0, await Dapper.SqlMapper.ExecuteScalarAsync<int>(
                connection, "SELECT COUNT(*) FROM ListingGenre;"));

            Assert.Equal(0, await Dapper.SqlMapper.ExecuteScalarAsync<int>(
                connection, "SELECT COUNT(*) FROM ListingDownload;"));

            Assert.Equal(0, await Dapper.SqlMapper.ExecuteScalarAsync<int>(
                connection, "SELECT COUNT(*) FROM ListingImage;"));
        }
    }

    [Fact]
    public async Task An_import_run_records_progress_and_can_be_resumed()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        var runId = await repository.StartRunAsync("test", ImportMode.Incremental);

        await repository.CheckpointRunAsync(new CatalogImportRun
        {
            RunId = runId,
            Cursor = "page-2",
            ItemsSeen = 100,
            ItemsChanged = 40
        });

        var open = await repository.GetLastRunAsync("test");

        Assert.NotNull(open);
        Assert.Equal("page-2", open.Cursor);
        Assert.Null(open.CompletedAt);

        await repository.CompleteRunAsync(new CatalogImportRun { RunId = runId, ItemsSeen = 200 });

        Assert.NotNull((await repository.GetLastRunAsync("test"))!.CompletedAt);
    }

    [Fact]
    public async Task A_search_pass_is_never_mistaken_for_the_last_import()
    {
        // The subtle one. A search is a completed run over the same source, but
        // it only covered what its term matched. Treated as the previous pass,
        // its start time becomes the incremental watermark and the next import
        // skips everything the search did not happen to match — items silently
        // missing from the catalogue, with nothing to indicate why.
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        var import = await repository.StartRunAsync("test", ImportMode.Incremental);
        await repository.CompleteRunAsync(new CatalogImportRun { RunId = import, ItemsSeen = 500 });

        var search = await repository.StartRunAsync("test", ImportMode.Search);
        await repository.CompleteRunAsync(new CatalogImportRun { RunId = search, ItemsSeen = 3 });

        var previous = await repository.GetLastRunAsync("test");

        Assert.NotNull(previous);
        Assert.Equal(ImportMode.Incremental, previous.Mode);
        Assert.Equal(import, previous.RunId);

        // The search is still recorded; it is just not evidence of coverage.
        Assert.Equal(500, previous.ItemsSeen);
    }

    [Fact]
    public async Task A_run_that_parsed_nothing_reports_a_zero_success_rate()
    {
        // The signal that a source has changed shape underneath a working parser.
        var run = new CatalogImportRun { ItemsChanged = 0, ItemsFailed = 25 };

        Assert.Equal(0d, run.ParseSuccessRate);
        Assert.Equal(1d, new CatalogImportRun().ParseSuccessRate);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_alias_binds_once_and_is_never_silently_rebound()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<ICatalogListingRepository>();

        await repository.UpsertManyAsync([Listing("lst_1", "Doom", 1993), Listing("lst_2", "Other", 1993)]);

        Assert.True(await repository.AddAliasAsync("doom|1993", "lst_1", "test"));
        Assert.False(await repository.AddAliasAsync("doom|1993", "lst_2", "test"));
        Assert.Equal("lst_1", await repository.ResolveAliasAsync("doom|1993"));
    }

    private static Task<CatalogListingPage> Search(ICatalogListingRepository repository, string text) =>
        repository.QueryAsync(new CatalogListingQuery { SearchText = text });

    private static CatalogListing Listing(
        string id,
        string title,
        int? year,
        Action<CatalogListing>? configure = null)
    {
        var listing = new CatalogListing
        {
            ListingId = id,
            Title = title,
            SortTitle = TitleNormalizer.ToSortTitle(title),
            Year = year,
            MatchKey = TitleNormalizer.ComputeMatchKey(title, year),
            PrimarySourceKey = "test"
        };

        configure?.Invoke(listing);

        // Stands in for the merger's hash so that "did anything change?" behaves
        // the way it does in production.
        listing.ContentHash = string.Join(
            '|',
            listing.Title,
            listing.Year,
            listing.Developer,
            listing.Publisher,
            listing.Description,
            string.Join(',', listing.Genres),
            string.Join(',', listing.Downloads.Select(download => download.Url)));

        return listing;
    }

    private static ListingDownload Download(string url, string? sha1 = null) =>
        new() { Url = url, Sha1 = sha1, SourceKey = "test" };

    private static ListingImage Image(string url, ListingImageKind kind) =>
        new() { RemoteUrl = url, Kind = kind, SourceKey = "test" };
}
