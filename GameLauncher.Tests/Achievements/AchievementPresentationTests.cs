using System.Text.Json;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.ViewModels;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Achievements;

/// <summary>
/// Covers how achievements are presented: concealment of hidden ones, progress,
/// grouping, and definitions whose provider is not installed.
/// </summary>
/// <remarks>
/// Concealment is asserted against the view model rather than the rendered view
/// on purpose. A template that simply declines to draw the title would still have
/// the real text bound into the visual tree; testing the boundary the view binds
/// to is what proves the secret never gets that far.
/// </remarks>
public sealed class AchievementPresentationTests
{
    [Fact]
    public void A_hidden_achievement_conceals_its_title_description_and_icon()
    {
        var definition = new AchievementDefinition
        {
            Id = 1,
            ApiName = "ACH_SECRET",
            Title = "Slew the hidden dragon",
            Description = "Defeat the dragon behind the waterfall.",
            IconPath = @"C:\art\dragon.png",
            IsHidden = true
        };

        var item = new AchievementItemViewModel(definition, unlockedAt: null);

        Assert.True(item.IsConcealed);
        Assert.Equal(AchievementItemViewModel.ConcealedTitle, item.DisplayTitle);
        Assert.Equal(AchievementItemViewModel.ConcealedDescription, item.DisplayDescription);
        Assert.Null(item.DisplayIconPath);

        // The real values must not leak through any property a view binds.
        Assert.DoesNotContain("dragon", item.DisplayTitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("waterfall", item.DisplayDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unlocking_a_hidden_achievement_reveals_it()
    {
        var definition = new AchievementDefinition
        {
            Id = 1,
            Title = "Slew the hidden dragon",
            Description = "Defeat the dragon behind the waterfall.",
            IconPath = @"C:\art\dragon.png",
            IsHidden = true
        };

        var item = new AchievementItemViewModel(definition, DateTimeOffset.Now);

        // Concealment ends at the moment of earning it, and never resumes.
        Assert.False(item.IsConcealed);
        Assert.Equal("Slew the hidden dragon", item.DisplayTitle);
        Assert.Equal("Defeat the dragon behind the waterfall.", item.DisplayDescription);
        Assert.Equal(@"C:\art\dragon.png", item.DisplayIconPath);
    }

    [Fact]
    public void A_hidden_achievement_discloses_no_progress()
    {
        var definition = new AchievementDefinition
        {
            Id = 1,
            Title = "Collect every relic",
            IsHidden = true,
            ProgressTarget = 50
        };

        var item = new AchievementItemViewModel(definition, unlockedAt: null, progressValue: 34);

        // "34 / 50" would give away both the goal and how close the player is.
        Assert.False(item.HasProgress);
        Assert.Equal(string.Empty, item.ProgressText);
    }

    [Fact]
    public void A_visible_achievement_reports_its_progress_against_the_target()
    {
        var definition = new AchievementDefinition
        {
            Id = 1,
            Title = "Ten hours in",
            ProgressTarget = 10
        };

        var item = new AchievementItemViewModel(definition, unlockedAt: null, progressValue: 4);

        Assert.True(item.HasProgress);
        Assert.Equal("4 / 10", item.ProgressText);
        Assert.Equal(40d, item.ProgressPercent, precision: 3);
    }

    [Fact]
    public void Progress_beyond_the_target_never_exceeds_a_full_bar()
    {
        var definition = new AchievementDefinition { Id = 1, Title = "Overshoot", ProgressTarget = 10 };
        var item = new AchievementItemViewModel(definition, unlockedAt: null, progressValue: 25);

        // A stat that keeps counting after the goal must not render past the end.
        Assert.Equal(100d, item.ProgressPercent, precision: 3);
    }

    [Fact]
    public void An_unlocked_achievement_shows_no_progress_bar()
    {
        var definition = new AchievementDefinition { Id = 1, Title = "Done", ProgressTarget = 10 };
        var item = new AchievementItemViewModel(definition, DateTimeOffset.Now, progressValue: 10);

        Assert.False(item.HasProgress);
    }

    [Fact]
    public void An_achievement_whose_provider_is_missing_says_so()
    {
        var definition = new AchievementDefinition { Id = 1, Title = "Orphan", ProviderKey = "gone" };

        var available = new AchievementItemViewModel(definition, null, isProviderAvailable: true);
        var missing = new AchievementItemViewModel(definition, null, isProviderAvailable: false);

        Assert.Null(available.ProviderWarning);
        Assert.NotNull(missing.ProviderWarning);
        Assert.Contains("gone", missing.ProviderWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_groups_by_title_and_keeps_hidden_achievements_concealed()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var games = host.Resolve<IGameRepository>();
        var achievements = host.Resolve<IAchievementRepository>();

        var entry = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        await games.AddAsync(new Game
        {
            Title = "Hollow Signal",
            CatalogId = entry.CatalogId,
            ExecutablePath = @"C:\Games\Hollow\game.exe",
            DateAdded = DateTimeOffset.Now,
            Tags = []
        });

        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_VISIBLE",
            Title = "First steps",
            ProviderKey = MetaAchievementProvider.ProviderKey,
            Kind = AchievementKind.Meta,
            TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig { Metric = MetaMetric.FirstLaunch })
        });

        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_SECRET",
            Title = "The secret ending",
            Description = "Reach the observatory before dawn.",
            IsHidden = true,
            ProviderKey = MetaAchievementProvider.ProviderKey,
            Kind = AchievementKind.Meta,
            TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig { Metric = MetaMetric.GameHours, Threshold = 99 })
        });

        // A library-wide achievement, which belongs to no title at all.
        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = null,
            ApiName = "ACH_COLLECTOR",
            Title = "Collector",
            ProviderKey = MetaAchievementProvider.ProviderKey,
            Kind = AchievementKind.Meta,
            TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig { Metric = MetaMetric.GamesOwned, Threshold = 25 })
        });

        var page = host.Resolve<AchievementsViewModel>();
        await page.LoadAsync();

        Assert.True(page.HasAny);
        Assert.Equal(2, page.Groups.Count);

        var gameGroup = page.Groups.Single(group => group.CatalogId == entry.CatalogId);
        Assert.Equal("Hollow Signal", gameGroup.Title);
        Assert.Equal(2, gameGroup.TotalCount);

        // Library-wide achievements sort last, under their own heading.
        var libraryGroup = page.Groups.Single(group => group.CatalogId is null);
        Assert.Equal(AchievementGroupViewModel.LibraryWideTitle, libraryGroup.Title);
        Assert.Equal(page.Groups[^1], libraryGroup);

        var secret = gameGroup.Items.Single(item => item.Definition.ApiName == "ACH_SECRET");
        Assert.True(secret.IsConcealed);
        Assert.Equal(AchievementItemViewModel.ConcealedTitle, secret.DisplayTitle);
        Assert.DoesNotContain("observatory", secret.DisplayDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_page_reports_progress_the_engine_recorded()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var games = host.Resolve<IGameRepository>();
        var achievements = host.Resolve<IAchievementRepository>();

        var entry = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        var game = new Game
        {
            Title = "Hollow Signal",
            CatalogId = entry.CatalogId,
            ExecutablePath = @"C:\Games\Hollow\game.exe",
            DateAdded = DateTimeOffset.Now,
            PlaytimeSeconds = 4 * 3600,
            LastPlayedAt = DateTimeOffset.Now,
            Tags = []
        };

        await games.AddAsync(game);

        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_HOURS",
            Title = "Ten hours",
            ProviderKey = MetaAchievementProvider.ProviderKey,
            Kind = AchievementKind.Meta,
            ProgressTarget = 10,
            TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig
            {
                Metric = MetaMetric.GameHours,
                Threshold = 10
            })
        });

        await host.Resolve<IAchievementEngine>().EvaluateGameAsync(game, AchievementTrigger.GameExited);

        var page = host.Resolve<AchievementsViewModel>();
        await page.LoadAsync();

        var item = page.Groups.SelectMany(group => group.Items).Single();

        Assert.False(item.IsUnlocked);
        Assert.True(item.HasProgress);
        Assert.Equal("4 / 10", item.ProgressText);
    }

    [Fact]
    public async Task Filtering_narrows_the_list_without_changing_the_reported_totals()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var achievements = host.Resolve<IAchievementRepository>();
        var entry = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        var earned = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_ONE",
            Title = "Earned",
            ProviderKey = MetaAchievementProvider.ProviderKey
        });

        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_TWO",
            Title = "Not earned",
            ProviderKey = MetaAchievementProvider.ProviderKey
        });

        await achievements.UnlockAsync(earned, DateTimeOffset.Now);

        var page = host.Resolve<AchievementsViewModel>();
        await page.LoadAsync();

        page.Filter = AchievementFilter.Unlocked;
        var unlockedOnly = page.Groups.Single();
        Assert.Single(unlockedOnly.Items);
        Assert.Equal("Earned", unlockedOnly.Items[0].Title);

        // The heading keeps reporting the real totals even when the list is cut
        // down, so "1 of 2" does not become a misleading "1 of 1".
        Assert.Equal(2, unlockedOnly.TotalCount);
        Assert.Equal("1 of 2 unlocked", unlockedOnly.SummaryText);

        page.Filter = AchievementFilter.Locked;
        Assert.Equal("Not earned", page.Groups.Single().Items.Single().Title);

        page.Filter = AchievementFilter.All;
        Assert.Equal(2, page.Groups.Single().Items.Count);
    }

    [Fact]
    public async Task A_definition_whose_provider_is_missing_is_flagged_but_left_intact()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var achievements = host.Resolve<IAchievementRepository>();
        var entry = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        var id = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_IMPORTED",
            Title = "Imported from elsewhere",
            ProviderKey = "steam-import",
            TriggerConfigJson = """{"appId":440}"""
        });

        await achievements.UnlockAsync(id, DateTimeOffset.Now.AddDays(-2));

        var page = host.Resolve<AchievementsViewModel>();
        await page.LoadAsync();

        var item = page.Groups.SelectMany(group => group.Items).Single();

        Assert.False(item.IsProviderAvailable);
        Assert.NotNull(item.ProviderWarning);
        Assert.NotNull(page.ProviderWarning);

        // Flagged, never rewritten: the key, the rule and the unlock all survive.
        var stored = await achievements.GetDefinitionByIdAsync(id);
        Assert.Equal("steam-import", stored!.ProviderKey);
        Assert.Equal("""{"appId":440}""", stored.TriggerConfigJson);
        Assert.True(item.IsUnlocked);
    }
}
