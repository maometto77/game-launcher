using System.Text.Json;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Achievements;

/// <summary>
/// Covers the evaluation engine's guarantees: idempotence, extensibility, and
/// the separation between deciding and recording.
/// </summary>
public sealed class AchievementEngineTests
{
    [Fact]
    public async Task Evaluation_unlocks_a_met_condition_and_raises_one_event()
    {
        using var host = new TestAppHost();
        var (game, _) = await SeedAsync(host, MetaMetric.FirstLaunch, threshold: 1, playedHours: 3);

        var engine = host.Resolve<IAchievementEngine>();
        var raised = new List<AchievementUnlockedEventArgs>();
        engine.AchievementUnlocked += (_, e) => raised.Add(e);

        var result = await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        Assert.Equal(1, result.Unlocked);
        Assert.Single(raised);
        Assert.Equal("ACH_TEST", raised[0].Definition.ApiName);
    }

    [Fact]
    public async Task Re_evaluating_never_duplicates_an_unlock_moves_its_timestamp_or_re_notifies()
    {
        using var host = new TestAppHost();
        var (game, definitionId) = await SeedAsync(host, MetaMetric.FirstLaunch, threshold: 1, playedHours: 3);

        var engine = host.Resolve<IAchievementEngine>();
        var achievements = host.Resolve<IAchievementRepository>();

        var raised = 0;
        engine.AchievementUnlocked += (_, _) => raised++;

        await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        var first = (await achievements.GetUnlocksAsync()).Single(u => u.DefinitionId == definitionId);

        // Runs the same pass repeatedly, exactly as the watcher does on every exit
        // and every startup.
        for (var pass = 0; pass < 5; pass++)
        {
            var repeat = await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);
            Assert.Equal(0, repeat.Unlocked);
        }

        var unlocks = await achievements.GetUnlocksAsync();
        var after = unlocks.Single(u => u.DefinitionId == definitionId);

        Assert.Single(unlocks);
        Assert.Equal(first.UnlockedAt, after.UnlockedAt);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task An_unmet_condition_records_progress_without_unlocking()
    {
        using var host = new TestAppHost();

        // Ten hours required, three played.
        var (game, definitionId) = await SeedAsync(host, MetaMetric.GameHours, threshold: 10, playedHours: 3);

        var engine = host.Resolve<IAchievementEngine>();
        var result = await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        Assert.Equal(0, result.Unlocked);
        Assert.Equal(1, result.ProgressUpdated);

        var progress = await host.Resolve<IAchievementRepository>().GetProgressAsync([definitionId]);
        Assert.Equal(3d, progress[definitionId].CurrentValue, precision: 3);
        Assert.Empty(await host.Resolve<IAchievementRepository>().GetUnlockedDefinitionIdsAsync());
    }

    [Fact]
    public async Task Progress_never_goes_backwards()
    {
        using var host = new TestAppHost();
        var (game, definitionId) = await SeedAsync(host, MetaMetric.GameHours, threshold: 100, playedHours: 20);

        var engine = host.Resolve<IAchievementEngine>();
        var achievements = host.Resolve<IAchievementRepository>();
        var games = host.Resolve<IGameRepository>();

        await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        // A rolled-back save, or a per-run counter that has just reset, must not
        // make a progress bar lose ground the player never lost.
        game.PlaytimeSeconds = (long)(2 * 3600);
        await games.UpdateAsync(game);

        await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        var progress = await achievements.GetProgressAsync([definitionId]);
        Assert.Equal(20d, progress[definitionId].CurrentValue, precision: 3);
    }

    [Fact]
    public async Task A_custom_provider_works_without_touching_the_engine()
    {
        using var host = new TestAppHost();
        var (game, definitionId) = await SeedAsync(
            host, MetaMetric.FirstLaunch, threshold: 1, playedHours: 0, providerKey: "custom-test");

        var custom = new AlwaysUnlockProvider();

        // Constructed with only the custom provider: the engine has no compiled
        // knowledge of any provider, so a new one is a registration and nothing
        // more.
        var engine = new AchievementEngine(
            [custom],
            host.Resolve<IAchievementRepository>(),
            new ImmediateDispatcher(),
            NullLogger<AchievementEngine>.Instance);

        var result = await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        Assert.Equal(1, result.Unlocked);
        Assert.Contains(definitionId, await host.Resolve<IAchievementRepository>().GetUnlockedDefinitionIdsAsync());
        Assert.True(custom.WasCalled);
    }

    [Fact]
    public async Task A_definition_whose_provider_is_not_installed_is_left_alone()
    {
        using var host = new TestAppHost();
        var (game, _) = await SeedAsync(
            host, MetaMetric.FirstLaunch, threshold: 1, playedHours: 5, providerKey: "not-installed");

        var engine = host.Resolve<IAchievementEngine>();
        var result = await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        // Inert rather than guessed at. Evaluating it with the wrong provider
        // would be worse than not evaluating it.
        Assert.Equal(0, result.Unlocked);
        Assert.Empty(await host.Resolve<IAchievementRepository>().GetUnlockedDefinitionIdsAsync());
    }

    [Fact]
    public async Task Two_providers_claiming_one_key_fail_at_construction()
    {
        using var host = new TestAppHost();

        // Caught at startup rather than silently letting one win, which would be
        // far harder to diagnose from the symptom.
        var exception = Assert.Throws<InvalidOperationException>(() => new AchievementEngine(
            [new AlwaysUnlockProvider(), new AlwaysUnlockProvider()],
            host.Resolve<IAchievementRepository>(),
            new ImmediateDispatcher(),
            NullLogger<AchievementEngine>.Instance));

        Assert.Contains("custom-test", exception.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Testing_a_definition_reports_a_verdict_without_recording_it()
    {
        using var host = new TestAppHost();
        var (game, definitionId) = await SeedAsync(host, MetaMetric.FirstLaunch, threshold: 1, playedHours: 4);

        var engine = host.Resolve<IAchievementEngine>();
        var definition = await host.Resolve<IAchievementRepository>().GetDefinitionByIdAsync(definitionId);

        var verdict = await engine.TestAsync(definition!, game);

        Assert.NotNull(verdict);
        Assert.True(verdict!.ShouldUnlock);

        // Somebody checking whether a rule is right must not thereby award
        // themselves the achievement.
        Assert.Empty(await host.Resolve<IAchievementRepository>().GetUnlockedDefinitionIdsAsync());
    }

    [Fact]
    public async Task Providers_are_skipped_for_triggers_they_do_not_handle()
    {
        using var host = new TestAppHost();
        var (game, _) = await SeedAsync(host, MetaMetric.FirstLaunch, threshold: 1, playedHours: 3);

        var engine = host.Resolve<IAchievementEngine>();

        // Meta metrics cannot change mid-session, so the running poll must not
        // re-read them several times a second.
        var polled = await engine.EvaluateGameAsync(game, AchievementTrigger.RunningPoll, processId: 1234);

        Assert.Equal(0, polled.Evaluated);
        Assert.Equal(0, polled.Unlocked);
    }

    /// <summary>
    /// Creates a catalogued game with one meta achievement.
    /// </summary>
    private static async Task<(Game Game, int DefinitionId)> SeedAsync(
        TestAppHost host,
        MetaMetric metric,
        double threshold,
        double playedHours,
        string? providerKey = null)
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
            PlaytimeSeconds = (long)(playedHours * 3600),
            LastPlayedAt = playedHours > 0 ? DateTimeOffset.Now : null,
            Tags = []
        };

        await games.AddAsync(game);

        var definitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_TEST",
            Title = "Test achievement",
            Kind = AchievementKind.Meta,
            ProviderKey = providerKey ?? MetaAchievementProvider.ProviderKey,
            ProgressTarget = threshold,
            TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig
            {
                Metric = metric,
                Threshold = threshold
            })
        });

        return (game, definitionId);
    }

    /// <summary>A provider that unlocks everything it is given.</summary>
    private sealed class AlwaysUnlockProvider : IAchievementProvider
    {
        public bool WasCalled { get; private set; }

        public string Key => "custom-test";

        public string DisplayName => "Custom test";

        public bool HandlesTrigger(AchievementTrigger trigger) => true;

        public Task<IReadOnlyList<AchievementEvaluation>> EvaluateAsync(
            IReadOnlyList<AchievementDefinition> definitions,
            AchievementEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;

            return Task.FromResult<IReadOnlyList<AchievementEvaluation>>(
                definitions.Select(d => AchievementEvaluation.Unlock(d.Id)).ToArray());
        }
    }

}
