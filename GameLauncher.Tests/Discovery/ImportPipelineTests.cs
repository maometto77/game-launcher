using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Import;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the pipeline that drives sources: what it fetches, what it declines to
/// fetch again, what it refuses to merge, and how it recovers from being killed.
/// </summary>
public sealed class ImportPipelineTests
{
    [Fact]
    public async Task An_import_creates_a_listing_per_game()
    {
        var source = new FakeCatalogSource()
            .Add("Doom", 1993, builder =>
            {
                builder.Developer = "id Software";
                builder.Genres = ["Shooter"];
                builder.Downloads = [Download("https://fake.test/doom.zip")];
            })
            .Add("SimCity", 1989);

        using var host = Host(source);

        var result = await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        Assert.Equal(2, result.ListingsAdded);
        Assert.Equal(2, await host.Resolve<ICatalogListingRepository>().CountAsync());

        var page = await host.Resolve<ICatalogListingRepository>()
            .QueryAsync(new CatalogListingQuery { SearchText = "doom" });

        var listing = page.Items.Single();

        Assert.Equal("id Software", listing.Developer);
        Assert.Equal(["Shooter"], listing.Genres);
        Assert.True(listing.IsDownloadable);
    }

    [Fact]
    public async Task Two_observations_of_one_game_in_the_same_batch_share_a_listing()
    {
        // Both are new, so neither is in the database when the other is placed.
        // Without the in-flight map they would each mint an identity.
        var first = new FakeCatalogSource("first").Add("Doom", 1993);
        var second = new FakeCatalogSource("second", rank: 1).Add("Doom", 1993, builder =>
            builder.Developer = "id Software");

        using var host = Host(first, second);

        await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        Assert.Equal(1, await host.Resolve<ICatalogListingRepository>().CountAsync());
    }

    [Fact]
    public async Task Metadata_from_two_sources_is_merged_onto_one_listing()
    {
        // A year apart, which is the ordinary case: one source records the
        // original release and the other a regional one. Two years apart is
        // deliberately not merged, and has its own test.
        var first = new FakeCatalogSource("first").Add("Doom", 1994, builder =>
            builder.Downloads = [Download("https://a.test/doom.zip")]);

        var second = new FakeCatalogSource("second", rank: 1).Add("Doom", 1993, builder =>
        {
            builder.Developer = "id Software";
            builder.Downloads = [Download("https://b.test/doom.zip")];
        });

        using var host = Host(first, second);

        await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        var page = await host.Resolve<ICatalogListingRepository>().QueryAsync(new CatalogListingQuery());
        var listing = await host.Resolve<ICatalogListingRepository>().GetAsync(page.Items[0].ListingId);

        Assert.NotNull(listing);
        Assert.Equal(1993, listing.Year);
        Assert.Equal("id Software", listing.Developer);
        Assert.Equal(2, listing.Downloads.Count);
    }

    [Fact]
    public async Task A_second_pass_fetches_nothing_when_nothing_changed()
    {
        var source = new FakeCatalogSource()
            .Add("Doom", 1993, builder => builder.UpdatedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .Add("SimCity", 1989, builder => builder.UpdatedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using var host = Host(source);
        var import = host.Resolve<ICatalogImportService>();

        await import.RunAsync(new ImportRunOptions());
        Assert.Equal(2, source.FetchCount);

        // The change stamp has not moved, so the pipeline skips both without a
        // single request. This is what makes an hourly refresh nearly free.
        var second = await import.RunAsync(new ImportRunOptions());

        Assert.Equal(2, source.FetchCount);
        Assert.Equal(0, second.ItemsChanged);
        Assert.False(second.HasChanges);
    }

    [Fact]
    public async Task A_re_fetch_that_returns_identical_content_writes_nothing()
    {
        // No change stamp at all, so the item is always fetched — but its content
        // hash is unchanged, so nothing is written.
        var source = new FakeCatalogSource().Add("Doom", 1993);

        using var host = Host(source);
        var import = host.Resolve<ICatalogImportService>();

        await import.RunAsync(new ImportRunOptions());
        var second = await import.RunAsync(new ImportRunOptions());

        Assert.Equal(2, source.FetchCount);
        Assert.Equal(0, second.ItemsChanged);
    }

    [Fact]
    public async Task A_changed_item_updates_its_listing()
    {
        var source = new FakeCatalogSource().Add("Doom", 1993);

        using var host = Host(source);
        var import = host.Resolve<ICatalogImportService>();

        await import.RunAsync(new ImportRunOptions());

        source.Replace("doom", listing => listing with { Developer = "id Software" });

        var second = await import.RunAsync(new ImportRunOptions());

        Assert.Equal(1, second.ItemsChanged);
        Assert.Equal(0, second.ListingsAdded);

        var page = await host.Resolve<ICatalogListingRepository>().QueryAsync(new CatalogListingQuery());

        Assert.Equal("id Software", page.Items.Single().Developer);
    }

    [Fact]
    public async Task A_title_matching_at_a_distant_year_is_flagged_rather_than_merged()
    {
        var source = new FakeCatalogSource()
            .Add("Prince of Persia", 1989, builder => builder.ItemId = "pop-1989")
            .Add("Prince of Persia", 2008, builder => builder.ItemId = "pop-2008");

        using var host = Host(source);

        var result = await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        // The remake is recorded as unplaceable rather than folded into the
        // original, because that fold cannot be undone from the merged row.
        Assert.Equal(1, result.ListingsAdded);
        Assert.Equal(1, result.Sources[0].ItemsFailed);
        Assert.Equal(1, await host.Resolve<ICatalogListingRepository>().CountAsync());
    }

    [Fact]
    public async Task A_collapsed_parse_rate_aborts_the_pass()
    {
        // Several batches' worth, so that stopping early is observable at all: a
        // batch is always fetched whole before it can be judged.
        const int Total = 400;

        var source = new FakeCatalogSource();

        for (var index = 0; index < Total; index++)
        {
            source.Add($"Game {index}", 1990 + index, builder => builder.ItemId = $"game-{index}");

            // A site redesign turns a working parser into one that returns
            // nothing. Continuing would burn the whole crawl budget and look
            // like a clean run that simply found nothing new.
            source.FailingItems.Add($"game-{index}");
        }

        using var host = Host(source);

        var result = await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        Assert.True(result.Sources[0].Aborted);
        Assert.NotNull(result.Sources[0].Error);
        Assert.Contains("changed shape", result.Sources[0].Error);
        Assert.True(source.FetchCount < Total, $"expected an early stop, but all {Total} were fetched");
    }

    [Fact]
    public async Task A_source_smaller_than_one_batch_is_still_health_checked()
    {
        // Found while writing these tests: the check originally ran only between
        // batches, so a source holding fewer items than one batch was never
        // judged at all and a totally broken parser reported a clean run.
        var source = new FakeCatalogSource();

        for (var index = 0; index < 30; index++)
        {
            source.Add($"Game {index}", 1990 + index, builder => builder.ItemId = $"game-{index}");
            source.FailingItems.Add($"game-{index}");
        }

        using var host = Host(source);

        var result = await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        Assert.True(result.Sources[0].Aborted);
        Assert.Contains("changed shape", result.Sources[0].Error);
    }

    [Fact]
    public async Task A_handful_of_failures_is_too_small_a_sample_to_judge()
    {
        // Below the sample floor nothing is concluded. The first few items of a
        // crawl are exactly where a legitimate odd one out turns up.
        var source = new FakeCatalogSource();

        for (var index = 0; index < 5; index++)
        {
            source.Add($"Game {index}", 1990 + index, builder => builder.ItemId = $"game-{index}");
            source.FailingItems.Add($"game-{index}");
        }

        using var host = Host(source);

        var result = await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        Assert.False(result.Sources[0].Aborted);
    }

    [Fact]
    public async Task A_healthy_pass_with_a_few_failures_is_not_aborted()
    {
        var source = new FakeCatalogSource();

        for (var index = 0; index < 60; index++)
        {
            source.Add($"Game {index}", 1990 + index, builder => builder.ItemId = $"game-{index}");
        }

        source.FailingItems.Add("game-3");
        source.ThrowingItems.Add("game-7");

        using var host = Host(source);

        var result = await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        Assert.False(result.Sources[0].Aborted);
        Assert.Equal(58, result.ListingsAdded);
    }

    [Fact]
    public async Task A_cancelled_pass_resumes_from_its_cursor()
    {
        var source = new FakeCatalogSource();

        for (var index = 0; index < 250; index++)
        {
            source.Add($"Game {index}", 1990 + index, builder => builder.ItemId = $"game-{index}");
        }

        var root = Path.Combine(Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));

        try
        {
            using (var host = Host(source, root: root))
            {
                using var cancellation = new CancellationTokenSource();

                // Cancel after the first batch has been written and checkpointed.
                source.OnYielded = index =>
                {
                    if (index >= 150)
                    {
                        cancellation.Cancel();
                    }
                };

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    host.Resolve<ICatalogImportService>()
                        .RunAsync(new ImportRunOptions(), cancellationToken: cancellation.Token));
            }

            source.OnYielded = null;
            var fetchedBeforeRestart = source.FetchCount;

            // A genuinely new container over the same database, as a restart is.
            using (var restarted = Host(source, root: root))
            {
                await restarted.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

                Assert.Equal(250, await restarted.Resolve<ICatalogListingRepository>().CountAsync());
            }

            // The second pass picked up where the first stopped rather than
            // re-fetching everything.
            Assert.True(
                source.FetchCount < 250 + fetchedBeforeRestart,
                $"expected a resume, but {source.FetchCount} fetches happened in total");

            Assert.NotNull(source.LastOptions?.Cursor);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_re_merge_contacts_no_source_at_all()
    {
        var source = new FakeCatalogSource().Add("Doom", 1993).Add("SimCity", 1989);

        using var host = Host(source);
        var import = host.Resolve<ICatalogImportService>();

        await import.RunAsync(new ImportRunOptions());
        var afterImport = source.FetchCount;

        var result = await import.RunAsync(new ImportRunOptions { Mode = ImportMode.Remerge });

        Assert.Equal(afterImport, source.FetchCount);
        Assert.Equal(0, source.EnumerateCount - 1);
        Assert.Equal(2, result.Sources[0].ItemsSeen);
    }

    [Fact]
    public async Task An_unavailable_source_is_skipped_rather_than_failing_the_pass()
    {
        var available = new FakeCatalogSource("available").Add("Doom", 1993);
        var unavailable = new FakeCatalogSource("unavailable") { IsAvailable = false };

        unavailable.Add("SimCity", 1989);

        using var host = Host(available, unavailable);

        var result = await host.Resolve<ICatalogImportService>().RunAsync(new ImportRunOptions());

        Assert.Single(result.Sources);
        Assert.Equal(0, unavailable.EnumerateCount);
    }

    [Fact]
    public async Task Only_the_named_source_runs_when_one_is_selected()
    {
        var first = new FakeCatalogSource("first").Add("Doom", 1993);
        var second = new FakeCatalogSource("second").Add("SimCity", 1989);

        using var host = Host(first, second);

        await host.Resolve<ICatalogImportService>()
            .RunAsync(new ImportRunOptions { SourceKeys = ["first"] });

        Assert.Equal(1, first.EnumerateCount);
        Assert.Equal(0, second.EnumerateCount);
    }

    [Fact]
    public async Task The_catalogue_updated_event_fires_only_when_something_changed()
    {
        var source = new FakeCatalogSource().Add("Doom", 1993);

        using var host = Host(source);
        var import = host.Resolve<ICatalogImportService>();

        var events = new List<CatalogUpdatedEventArgs>();
        import.CatalogUpdated += (_, e) => events.Add(e);

        await import.RunAsync(new ImportRunOptions());
        Assert.Single(events);
        Assert.Equal(1, events[0].ListingsAdded);

        // Nothing changed the second time, so nothing is announced. A banner that
        // appeared on every scheduled refresh would train the user to ignore it.
        await import.RunAsync(new ImportRunOptions());
        Assert.Single(events);
    }

    [Fact]
    public async Task Two_sources_claiming_one_key_fail_at_construction()
    {
        // The same guarantee the achievement engine makes: a silent collision
        // would attribute rows to whichever source won.
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var host = Host(new FakeCatalogSource("clash"), new FakeCatalogSource("clash"));
            host.Resolve<ICatalogImportService>();
        });

        Assert.Contains("clash", exception.Message);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_second_concurrent_pass_is_refused()
    {
        var source = new FakeCatalogSource();

        for (var index = 0; index < 200; index++)
        {
            source.Add($"Game {index}", 1990 + index, builder => builder.ItemId = $"game-{index}");
        }

        using var host = Host(source);
        var import = host.Resolve<ICatalogImportService>();

        var first = import.RunAsync(new ImportRunOptions());

        // Two passes writing the same rows would each re-fetch what the other was
        // already fetching.
        await Assert.ThrowsAsync<InvalidOperationException>(() => import.RunAsync(new ImportRunOptions()));

        await first;
        Assert.False(import.IsRunning);
    }

    [Fact]
    public async Task Progress_is_reported_as_the_pass_runs()
    {
        var source = new FakeCatalogSource();

        for (var index = 0; index < 250; index++)
        {
            source.Add($"Game {index}", 1990 + index, builder => builder.ItemId = $"game-{index}");
        }

        using var host = Host(source);

        var reports = new List<ImportProgress>();

        await host.Resolve<ICatalogImportService>()
            .RunAsync(new ImportRunOptions(), new Progress<ImportProgress>(reports.Add));

        // Progress arrives on the captured context; give it a moment to drain.
        await Task.Delay(50);

        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task Max_items_bounds_a_pass()
    {
        var source = new FakeCatalogSource();

        for (var index = 0; index < 250; index++)
        {
            source.Add($"Game {index}", 1990 + index, builder => builder.ItemId = $"game-{index}");
        }

        using var host = Host(source);

        var result = await host.Resolve<ICatalogImportService>()
            .RunAsync(new ImportRunOptions { MaxItems = 50 });

        Assert.Equal(50, result.Sources[0].ItemsSeen);
    }

    private static ListingDownloadRef Download(string url) => new() { Url = new Uri(url) };

    private static TestAppHost Host(params ICatalogSource[] sources) => Host(sources, null);

    private static TestAppHost Host(ICatalogSource source, string? root) => Host([source], root);

    private static TestAppHost Host(ICatalogSource[] sources, string? root) =>
        new(root, migrate: true, configure: services =>
        {
            foreach (var source in sources)
            {
                services.AddSingleton(source);
            }
        });
}
