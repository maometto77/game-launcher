using System.Diagnostics;
using System.Text.Json;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Notifications;
using GameLauncher.Desktop.ViewModels;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Achievements;

/// <summary>
/// Covers announcement of earned achievements: that every one is shown, that they
/// are shown one at a time in order, and that nothing but a genuine unlock can
/// produce one.
/// </summary>
/// <remarks>
/// Driven through the real engine and the real repository, because the guarantee
/// under test is the whole chain — evaluate, record the transition, announce once
/// — rather than any single link in it.
/// </remarks>
public sealed class AchievementToastTests
{
    /// <summary>Dwell used in these tests, short enough to keep a run brief.</summary>
    private static readonly TimeSpan TestDwell = TimeSpan.FromMilliseconds(80);

    [Fact]
    public async Task An_earned_achievement_is_announced_once()
    {
        using var host = new TestAppHost();
        var game = await SeedAsync(host, ("ACH_ONE", MetaMetric.FirstLaunch, 1));

        await using var announcer = await StartAsync(host);

        await announcer.Engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);
        await announcer.DrainAsync(expected: 1);

        Assert.Equal(["ACH_ONE"], announcer.Announced);
    }

    [Fact]
    public async Task Repeated_evaluation_never_announces_the_same_achievement_twice()
    {
        using var host = new TestAppHost();
        var game = await SeedAsync(host, ("ACH_ONE", MetaMetric.FirstLaunch, 1));

        await using var announcer = await StartAsync(host);
        var engine = announcer.Engine;

        // Exactly what the watcher does: the same pass runs on every exit and
        // every startup, against a condition that stays true forever after.
        for (var pass = 0; pass < 6; pass++)
        {
            await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);
        }

        await announcer.DrainAsync(expected: 1);

        Assert.Equal(["ACH_ONE"], announcer.Announced);
    }

    [Fact]
    public async Task Several_achievements_earned_at_once_are_announced_one_at_a_time()
    {
        using var host = new TestAppHost();

        var game = await SeedAsync(
            host,
            ("ACH_ONE", MetaMetric.FirstLaunch, 1),
            ("ACH_TWO", MetaMetric.GameHours, 1),
            ("ACH_THREE", MetaMetric.Sessions, 0));

        await using var announcer = await StartAsync(host);

        var stopwatch = Stopwatch.StartNew();
        var result = await announcer.Engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        Assert.Equal(3, result.Unlocked);

        await announcer.DrainAsync(expected: 3);
        stopwatch.Stop();

        // All three shown, each exactly once, in the order the set was authored.
        Assert.Equal(["ACH_ONE", "ACH_TWO", "ACH_THREE"], announcer.Announced);

        // Serialised rather than overlapping. An implementation that let a new
        // unlock replace the one on screen would finish this in about no time;
        // showing three in turn cannot take less than two dwells.
        Assert.True(
            stopwatch.Elapsed >= TestDwell + TestDwell,
            $"Three announcements drained in {stopwatch.ElapsedMilliseconds} ms, which is too fast to have been shown in turn.");

        // The queue is empty by the time the last one is shown. The earlier counts
        // are deliberately not asserted: whether an unlock is queued before or
        // after the pump picks up the previous one is a real race — [2,1,0] and
        // [0,1,0] are both correct — and only the drained end state is invariant.
        var pending = announcer.Snapshots.Select(snapshot => snapshot.Pending).ToArray();
        Assert.Equal(0, pending[^1]);
    }

    [Fact]
    public async Task Dismissing_an_announcement_moves_straight_to_the_next()
    {
        using var host = new TestAppHost();

        var game = await SeedAsync(
            host,
            ("ACH_ONE", MetaMetric.FirstLaunch, 1),
            ("ACH_TWO", MetaMetric.GameHours, 1));

        await using var announcer = await StartAsync(host);

        // Long enough that draining within the timeout is only possible if
        // dismissing genuinely cuts the wait short.
        announcer.Service.Dwell = TimeSpan.FromSeconds(30);
        announcer.Service.BacklogDwellTime = TimeSpan.FromSeconds(30);

        await announcer.Engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

        await announcer.WaitForAsync(() => announcer.Announced.Count == 1);
        announcer.Service.DismissCurrent();

        await announcer.WaitForAsync(() => announcer.Announced.Count == 2);
        announcer.Service.DismissCurrent();

        await announcer.WaitForAsync(() => announcer.Service.Current is null);

        Assert.Equal(2, announcer.Announced.Count);
    }

    [Fact]
    public async Task The_overlay_shows_whatever_the_service_says_is_current()
    {
        using var host = new TestAppHost();

        var game = await SeedAsync(
            host,
            ("ACH_ONE", MetaMetric.FirstLaunch, 1),
            ("ACH_TWO", MetaMetric.GameHours, 1));

        await using var announcer = await StartAsync(host);

        using var overlay = new AchievementToastHostViewModel(announcer.Service, new ImmediateDispatcher());

        Assert.False(overlay.IsVisible);
        Assert.Null(overlay.Current);

        announcer.Service.Dwell = TimeSpan.FromSeconds(30);
        announcer.Service.BacklogDwellTime = TimeSpan.FromSeconds(30);

        await announcer.Engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);
        await announcer.WaitForAsync(() => overlay.IsVisible);

        Assert.NotNull(overlay.Current);
        Assert.Equal("ACH_ONE", overlay.Current!.Title);
        Assert.Contains("Hollow Signal", overlay.Current.Heading, StringComparison.Ordinal);

        // Dismissing advances the overlay to the second announcement...
        overlay.DismissCommand.Execute(null);
        await announcer.WaitForAsync(() => overlay.Current?.Title == "ACH_TWO");

        // ...and dismissing that one clears it, because nothing follows.
        Assert.Null(overlay.PendingText);

        overlay.DismissCommand.Execute(null);
        await announcer.WaitForAsync(() => !overlay.IsVisible);

        Assert.Null(overlay.Current);
        Assert.Null(overlay.PendingText);
    }

    [Fact]
    public async Task Testing_a_rule_records_nothing_and_announces_nothing()
    {
        using var host = new TestAppHost();
        var game = await SeedAsync(host, ("ACH_ONE", MetaMetric.FirstLaunch, 1));

        await using var announcer = await StartAsync(host);

        var achievements = host.Resolve<IAchievementRepository>();
        var definition = (await achievements.GetAllDefinitionsAsync()).Single();

        var verdict = await announcer.Engine.TestAsync(definition, game);

        // The rule is satisfied, so anything that persisted on the strength of a
        // verdict would have done so here.
        Assert.NotNull(verdict);
        Assert.True(verdict!.ShouldUnlock);

        Assert.Empty(await achievements.GetUnlockedDefinitionIdsAsync());
        Assert.Empty(await achievements.GetProgressAsync([definition.Id]));

        // Given time to arrive, so this is an absence rather than a race.
        await Task.Delay(TestDwell);
        Assert.Empty(announcer.Announced);
        Assert.Null(announcer.Service.Current);
    }

    /// <summary>
    /// Builds an engine and notification service, starts the service, and records
    /// everything it announces.
    /// </summary>
    /// <param name="host">The container supplying the real providers and repository.</param>
    /// <returns>A recorder that stops the service when disposed.</returns>
    /// <remarks>
    /// The engine is constructed here rather than resolved so its dispatcher can
    /// be substituted. The container's dispatcher binds to the WPF test host's
    /// thread whenever one exists in the process, which would make these tests
    /// marshal onto — and wait on — a dispatcher owned by a different test
    /// collection. Everything else is the real thing.
    /// </remarks>
    private static async Task<Announcements> StartAsync(TestAppHost host)
    {
        var engine = new AchievementEngine(
            host.Services.GetRequiredService<IEnumerable<IAchievementProvider>>(),
            host.Resolve<IAchievementRepository>(),
            new ImmediateDispatcher(),
            NullLogger<AchievementEngine>.Instance);

        var service = new AchievementNotificationService(
            engine, NullLogger<AchievementNotificationService>.Instance)
        {
            Dwell = TestDwell,
            BacklogDwellTime = TestDwell
        };

        await service.StartAsync(CancellationToken.None);

        return new Announcements(engine, service);
    }

    /// <summary>
    /// Creates a catalogued game with one already-satisfied meta achievement per
    /// entry.
    /// </summary>
    /// <param name="host">The container to resolve from.</param>
    /// <param name="definitions">Api name, metric and threshold for each achievement.</param>
    /// <returns>The seeded game.</returns>
    private static async Task<Game> SeedAsync(
        TestAppHost host,
        params (string ApiName, MetaMetric Metric, double Threshold)[] definitions)
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
            PlaytimeSeconds = 6 * 3600,
            LastPlayedAt = DateTimeOffset.Now,
            Tags = []
        };

        await games.AddAsync(game);

        var order = 0;

        foreach (var (apiName, metric, threshold) in definitions)
        {
            await achievements.AddDefinitionAsync(new AchievementDefinition
            {
                CatalogId = entry.CatalogId,
                ApiName = apiName,
                Title = apiName,
                Kind = AchievementKind.Meta,
                ProviderKey = MetaAchievementProvider.ProviderKey,
                SortOrder = order++,
                TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig
                {
                    Metric = metric,
                    Threshold = threshold
                })
            });
        }

        return game;
    }

    /// <summary>
    /// Records what the notification service announces, in order.
    /// </summary>
    private sealed class Announcements : IAsyncDisposable
    {
        private readonly List<string> _announced = [];
        private readonly List<(string? Title, int Pending)> _snapshots = [];
        private readonly object _gate = new();

        public Announcements(AchievementEngine engine, AchievementNotificationService service)
        {
            Engine = engine;
            Service = service;
            Service.CurrentChanged += OnCurrentChanged;
        }

        /// <summary>Gets the engine the service is listening to.</summary>
        public AchievementEngine Engine { get; }

        /// <summary>Gets the service under test.</summary>
        public AchievementNotificationService Service { get; }

        /// <summary>Gets the api names announced, in the order they appeared.</summary>
        public IReadOnlyList<string> Announced
        {
            get
            {
                lock (_gate)
                {
                    return _announced.ToArray();
                }
            }
        }

        /// <summary>Gets each non-empty state the service published.</summary>
        public IReadOnlyList<(string? Title, int Pending)> Snapshots
        {
            get
            {
                lock (_gate)
                {
                    return _snapshots.ToArray();
                }
            }
        }

        /// <summary>Waits until every expected announcement has been shown and the queue is empty.</summary>
        /// <param name="expected">How many announcements to wait for.</param>
        public Task DrainAsync(int expected) =>
            WaitForAsync(() => Announced.Count >= expected && Service.Current is null);

        /// <summary>Polls until a condition holds, or fails the test.</summary>
        /// <param name="condition">The condition to wait for.</param>
        /// <exception cref="Xunit.Sdk.XunitException">The condition did not hold in time.</exception>
        public async Task WaitForAsync(Func<bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Fail($"Timed out. Announced so far: [{string.Join(", ", Announced)}].");
        }

        private void OnCurrentChanged(object? sender, AchievementNotificationChangedEventArgs e)
        {
            if (e.Current is null)
            {
                return;
            }

            lock (_gate)
            {
                _announced.Add(e.Current.Definition.ApiName);
                _snapshots.Add((e.Current.Definition.ApiName, e.PendingCount));
            }
        }

        public async ValueTask DisposeAsync()
        {
            Service.CurrentChanged -= OnCurrentChanged;
            await Service.StopAsync(CancellationToken.None);
        }
    }
}
