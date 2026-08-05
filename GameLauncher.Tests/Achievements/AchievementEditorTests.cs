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
/// Covers the achievement editor: that it authors definitions, that testing a
/// rule stays inert, and that it never damages a definition whose provider is not
/// installed.
/// </summary>
public sealed class AchievementEditorTests
{
    [Fact]
    public async Task The_editor_offers_exactly_the_providers_that_are_installed()
    {
        using var host = new TestAppHost();

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(null);

        var offered = editor.Providers.Select(provider => provider.Key).ToHashSet(StringComparer.Ordinal);
        var installed = host.Resolve<IAchievementEngine>().Providers
            .Select(provider => provider.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(installed, offered);
        Assert.Contains(MetaAchievementProvider.ProviderKey, offered);
        Assert.Contains(ManualAchievementProvider.ProviderKey, offered);

        // A new achievement starts on a provider that always works, so the dialog
        // opens in a state that can actually be saved.
        Assert.Equal(MetaAchievementProvider.ProviderKey, editor.SelectedProvider?.Key);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Testing_a_rule_writes_nothing_even_when_the_condition_is_met()
    {
        using var host = new TestAppHost();
        var (game, _) = await SeedAsync(host);

        var achievements = host.Resolve<IAchievementRepository>();
        var engine = host.Resolve<IAchievementEngine>();

        var announced = 0;
        engine.AchievementUnlocked += (_, _) => announced++;

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(null);
        await editor.OnNavigatedToAsync();

        editor.Title = "Getting started";
        editor.ApiName = "ACH_UNSAVED";
        editor.SelectedProvider = editor.Providers.Single(
            provider => provider.Key == MetaAchievementProvider.ProviderKey);
        editor.MetaMetric = MetaMetric.FirstLaunch;
        editor.SelectedTarget = editor.Targets.Single(target => target.CatalogId == game.CatalogId);

        await editor.TestCommand.ExecuteAsync(null);

        // The rule is satisfied, which is precisely the case where a careless
        // implementation would award it.
        Assert.True(editor.TestSucceeded);
        Assert.Contains("test", editor.TestResult!, StringComparison.OrdinalIgnoreCase);

        // Nothing reached storage: no definition, no unlock, no progress.
        Assert.DoesNotContain(
            await achievements.GetAllDefinitionsAsync(),
            definition => definition.ApiName == "ACH_UNSAVED");

        Assert.Empty(await achievements.GetUnlockedDefinitionIdsAsync());
        Assert.Equal(0, await achievements.GetUnlockCountAsync());
        Assert.Equal(0, announced);
    }

    [Fact]
    public async Task Testing_an_existing_rule_leaves_its_stored_state_untouched()
    {
        using var host = new TestAppHost();
        var (game, definitionId) = await SeedAsync(host);

        var achievements = host.Resolve<IAchievementRepository>();
        var stored = await achievements.GetDefinitionByIdAsync(definitionId);

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(stored);
        await editor.OnNavigatedToAsync();

        // Edited on screen but deliberately not saved, so a test that persisted
        // would leave the changed rule behind.
        editor.Title = "Renamed in the editor";
        editor.MetaThresholdText = "999";
        editor.SelectedTarget = editor.Targets.Single(target => target.CatalogId == game.CatalogId);

        await editor.TestCommand.ExecuteAsync(null);

        var afterTest = await achievements.GetDefinitionByIdAsync(definitionId);

        Assert.Equal("Getting started", afterTest!.Title);
        Assert.Equal(stored!.TriggerConfigJson, afterTest.TriggerConfigJson);
        Assert.Empty(await achievements.GetUnlockedDefinitionIdsAsync());
    }

    [Fact]
    public async Task Testing_reports_why_a_rule_could_not_be_read_rather_than_failing()
    {
        using var host = new TestAppHost();
        var (game, _) = await SeedAsync(host);

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(null);
        await editor.OnNavigatedToAsync();

        editor.Title = "Reads a save";
        editor.SelectedProvider = editor.Providers.Single(
            provider => provider.Key == SaveFileAchievementProvider.ProviderKey);
        editor.SelectedTarget = editor.Targets.Single(target => target.CatalogId == game.CatalogId);
        editor.SaveFilePath = Path.Combine(Path.GetTempPath(), "GameLauncherTests", "no-such-save.json");
        editor.FieldPath = "progress.chapters";
        editor.TargetValue = "12";

        await editor.TestCommand.ExecuteAsync(null);

        // A configuration problem is worth reporting; it is not an exception.
        Assert.False(editor.TestSucceeded);
        Assert.True(editor.HasTestResult);
        Assert.Contains("exist", editor.TestResult!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Testing_a_memory_rule_explains_that_the_game_is_not_running()
    {
        using var host = new TestAppHost();
        var (game, _) = await SeedAsync(host);

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(null);
        await editor.OnNavigatedToAsync();

        editor.Title = "Reads memory";
        editor.SelectedProvider = editor.Providers.Single(
            provider => provider.Key == MemoryAchievementProvider.ProviderKey);
        editor.SelectedTarget = editor.Targets.Single(target => target.CatalogId == game.CatalogId);
        editor.ModuleName = "game.exe";
        editor.Offset = "0x1000";
        editor.TargetValue = "100";

        await editor.TestCommand.ExecuteAsync(null);

        Assert.False(editor.TestSucceeded);
        Assert.Contains("not running", editor.TestResult!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_authors_a_new_achievement_against_the_chosen_title()
    {
        using var host = new TestAppHost();
        var (game, _) = await SeedAsync(host);

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(null);
        await editor.OnNavigatedToAsync();

        editor.Title = "Marathon";
        editor.ApiName = "ACH_MARATHON";
        editor.Description = "Play for twenty hours.";
        editor.IsHidden = true;
        editor.ProgressTargetText = "20";
        editor.SelectedProvider = editor.Providers.Single(
            provider => provider.Key == MetaAchievementProvider.ProviderKey);
        editor.MetaMetric = MetaMetric.GameHours;
        editor.MetaThresholdText = "20";
        editor.SelectedTarget = editor.Targets.Single(target => target.CatalogId == game.CatalogId);

        var closed = false;
        editor.CloseRequested += (_, result) => closed = result;

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);

        var saved = (await host.Resolve<IAchievementRepository>().GetAllDefinitionsAsync())
            .Single(definition => definition.ApiName == "ACH_MARATHON");

        Assert.Equal(game.CatalogId, saved.CatalogId);
        Assert.Equal(MetaAchievementProvider.ProviderKey, saved.ProviderKey);
        Assert.Equal(AchievementKind.Meta, saved.Kind);
        Assert.True(saved.IsHidden);
        Assert.Equal(20d, saved.ProgressTarget);

        // The repository assigns identity; the editor never invents one.
        Assert.NotEmpty(saved.GlobalKey);

        var rule = MetaTriggerConfig.TryParse(saved.TriggerConfigJson);
        Assert.NotNull(rule);
        Assert.Equal(MetaMetric.GameHours, rule!.Metric);
        Assert.Equal(20d, rule.Threshold);
    }

    [Fact]
    public async Task An_incomplete_rule_is_refused_rather_than_stored_half_built()
    {
        using var host = new TestAppHost();
        await SeedAsync(host);

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(null);
        await editor.OnNavigatedToAsync();

        editor.Title = "Broken";
        editor.SelectedProvider = editor.Providers.Single(
            provider => provider.Key == SaveFileAchievementProvider.ProviderKey);

        // No save file path, no field path, no target value.
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.HasError);
        Assert.DoesNotContain(
            await host.Resolve<IAchievementRepository>().GetAllDefinitionsAsync(),
            definition => definition.Title == "Broken");
    }

    [Fact]
    public async Task Opening_a_definition_whose_provider_is_gone_preserves_it_exactly()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var achievements = host.Resolve<IAchievementRepository>();
        var entry = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        const string Rule = """{"appId":440,"statName":"kills"}""";

        var id = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_IMPORTED",
            Title = "Imported",
            ProviderKey = "steam-import",
            TriggerConfigJson = Rule
        });

        var unlockedAt = DateTimeOffset.Now.AddDays(-3);
        await achievements.UnlockAsync(id, unlockedAt);

        var stored = await achievements.GetDefinitionByIdAsync(id);

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(stored);
        await editor.OnNavigatedToAsync();

        // Nothing installed claims this key, so the editor says so and offers no
        // selection rather than quietly picking one.
        Assert.True(editor.IsProviderMissing);
        Assert.Equal("steam-import", editor.MissingProviderKey);
        Assert.Null(editor.SelectedProvider);

        // Saving without choosing a provider must leave the definition as it was:
        // same key, same rule, and the unlock still recorded.
        editor.Description = "Edited description";
        await editor.SaveCommand.ExecuteAsync(null);

        var after = await achievements.GetDefinitionByIdAsync(id);

        Assert.Equal("steam-import", after!.ProviderKey);
        Assert.Equal(Rule, after.TriggerConfigJson);
        Assert.Equal("Edited description", after.Description);

        var unlock = (await achievements.GetUnlocksAsync()).Single(entry => entry.DefinitionId == id);
        Assert.Equal(unlockedAt.ToUnixTimeSeconds(), unlock.UnlockedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Testing_a_definition_with_no_installed_provider_says_so_and_does_nothing()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var achievements = host.Resolve<IAchievementRepository>();
        var entry = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        var id = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_IMPORTED",
            Title = "Imported",
            ProviderKey = "steam-import"
        });

        var editor = host.Resolve<AchievementEditorViewModel>();
        editor.Initialize(await achievements.GetDefinitionByIdAsync(id));
        await editor.OnNavigatedToAsync();

        await editor.TestCommand.ExecuteAsync(null);

        Assert.False(editor.TestSucceeded);
        Assert.Contains("steam-import", editor.TestResult!, StringComparison.Ordinal);
        Assert.Empty(await achievements.GetUnlockedDefinitionIdsAsync());
    }

    /// <summary>
    /// Creates a catalogued, already-played game with one meta achievement.
    /// </summary>
    /// <param name="host">The container to resolve from.</param>
    /// <returns>The seeded game and the identifier of its achievement.</returns>
    private static async Task<(Game Game, int DefinitionId)> SeedAsync(TestAppHost host)
    {
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
            PlaytimeSeconds = 5 * 3600,
            LastPlayedAt = DateTimeOffset.Now,
            Tags = []
        };

        await games.AddAsync(game);

        var definitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_START",
            Title = "Getting started",
            Kind = AchievementKind.Meta,
            ProviderKey = MetaAchievementProvider.ProviderKey,
            TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig
            {
                Metric = MetaMetric.FirstLaunch
            })
        });

        return (game, definitionId);
    }
}
