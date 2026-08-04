using System.Text.Json;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Friends;
using GameLauncher.Shared.Contracts;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Achievements;

/// <summary>
/// The complete offline-first journey: earn an achievement with no relay, close
/// the launcher, reopen it, reconnect, and synchronise.
/// </summary>
/// <remarks>
/// The scenario that has to work for an offline-first launcher to mean anything.
/// Each step is exercised against a real database, and the restart is a genuinely
/// new container over the same files, so nothing survives on in-memory state.
/// </remarks>
public sealed class OfflineUnlockFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Unlock_offline_restart_reconnect_and_synchronise()
    {
        var api = new RecordingRelayApi { IsOffline = true };
        DateTimeOffset unlockedAt;
        string provisionalCatalogId;
        int definitionId;
        int gameId;

        // ---- Session one: entirely offline -------------------------------
        using (var host = new TestAppHost(_root))
        {
            var (game, definition) = await SeedAsync(host);
            gameId = game.Id;
            definitionId = definition;
            provisionalCatalogId = game.CatalogId!;

            // No relay: the catalog identity is provisional and nothing is pushed.
            Assert.StartsWith(CatalogEntry.ProvisionalPrefix, provisionalCatalogId, StringComparison.Ordinal);

            var engine = host.Resolve<IAchievementEngine>();
            var raised = new List<AchievementUnlockedEventArgs>();
            engine.AchievementUnlocked += (_, e) => raised.Add(e);

            var result = await engine.EvaluateGameAsync(game, AchievementTrigger.GameExited);

            Assert.Equal(1, result.Unlocked);
            Assert.Single(raised);

            unlockedAt = raised[0].UnlockedAt;

            // Queued, but not eligible to push: the relay has never heard of a
            // provisional identity.
            var achievements = host.Resolve<IAchievementRepository>();
            Assert.Contains(definitionId, await achievements.GetUnlockedDefinitionIdsAsync());
            Assert.Empty(await achievements.GetUnsyncedUnlocksAsync(10));

            // A sync attempt while offline changes nothing.
            var sync = BuildSync(host, api);
            var offlinePass = await sync.SynchronizeAsync();

            Assert.False(offlinePass.Completed);
            Assert.Empty(api.PushedUnlocks);
        }

        // ---- Session two: the launcher restarts --------------------------
        using (var host = new TestAppHost(_root))
        {
            var achievements = host.Resolve<IAchievementRepository>();

            // The unlock survived the restart with its timestamp intact.
            var unlock = (await achievements.GetUnlocksAsync()).Single(u => u.DefinitionId == definitionId);
            Assert.Equal(unlockedAt.ToUnixTimeSeconds(), unlock.UnlockedAt.ToUnixTimeSeconds());
            Assert.Null(unlock.SyncedAt);

            // Re-evaluating on startup must not disturb it.
            var game = await host.Resolve<IGameRepository>().GetByIdAsync(gameId);
            var engine = host.Resolve<IAchievementEngine>();

            var reNotified = 0;
            engine.AchievementUnlocked += (_, _) => reNotified++;

            await engine.EvaluateGameAsync(game!, AchievementTrigger.Startup);

            var afterRestart = (await achievements.GetUnlocksAsync()).Single(u => u.DefinitionId == definitionId);
            Assert.Equal(unlock.UnlockedAt, afterRestart.UnlockedAt);
            Assert.Equal(0, reNotified);

            // ---- The relay comes back ------------------------------------
            api.IsOffline = false;

            var pass = await BuildSync(host, api).SynchronizeAsync();

            Assert.True(pass.Completed);
            Assert.Equal(1, pass.CatalogEntriesPromoted);
            Assert.Equal(1, pass.UnlocksPushed);

            // Pushed under the assigned identity, never the provisional one.
            var pushed = Assert.Single(api.PushedUnlocks);
            Assert.StartsWith("app_", pushed.CatalogId, StringComparison.Ordinal);
            Assert.Equal("ACH_TEST", pushed.ApiName);
            Assert.NotEqual(provisionalCatalogId, pushed.CatalogId);

            // The timestamp that crossed the wire is the one earned offline.
            Assert.Equal(unlockedAt.ToUnixTimeSeconds(), pushed.UnlockedAt.ToUnixTimeSeconds());

            // Stamped, so it leaves the queue.
            Assert.Empty(await achievements.GetUnsyncedUnlocksAsync(10));
            Assert.NotNull((await achievements.GetUnlocksAsync())
                .Single(u => u.DefinitionId == definitionId).SyncedAt);

            // ---- Idempotency ---------------------------------------------
            var second = await BuildSync(host, api).SynchronizeAsync();

            Assert.False(second.DidWork);
            Assert.Single(api.PushedUnlocks);

            var finalUnlock = (await achievements.GetUnlocksAsync()).Single(u => u.DefinitionId == definitionId);
            Assert.Equal(unlockedAt.ToUnixTimeSeconds(), finalUnlock.UnlockedAt.ToUnixTimeSeconds());
        }
    }

    [Fact]
    public async Task Progress_earned_offline_survives_a_restart()
    {
        using (var host = new TestAppHost(_root))
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
                Kind = AchievementKind.Meta,
                ProviderKey = MetaAchievementProvider.ProviderKey,
                ProgressTarget = 10,
                TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig
                {
                    Metric = MetaMetric.GameHours,
                    Threshold = 10
                })
            });

            await host.Resolve<IAchievementEngine>()
                .EvaluateGameAsync(game, AchievementTrigger.GameExited);
        }

        using (var host = new TestAppHost(_root))
        {
            var achievements = host.Resolve<IAchievementRepository>();
            var definitions = await achievements.GetAllDefinitionsAsync();
            var progress = await achievements.GetProgressAsync(definitions.Select(d => d.Id).ToArray());

            // Four of ten hours, still recorded after the restart, still locked.
            Assert.Equal(4d, progress[definitions[0].Id].CurrentValue, precision: 3);
            Assert.Empty(await achievements.GetUnlockedDefinitionIdsAsync());
        }
    }

    /// <summary>Creates a catalogued game with one meta achievement already satisfied.</summary>
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
            PlaytimeSeconds = 3 * 3600,
            LastPlayedAt = DateTimeOffset.Now,
            Tags = []
        };

        await games.AddAsync(game);

        var definitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_TEST",
            Title = "Getting started",

            // Hidden achievements must synchronise exactly like any other.
            IsHidden = true,
            Kind = AchievementKind.Meta,
            ProviderKey = MetaAchievementProvider.ProviderKey,
            TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig
            {
                Metric = MetaMetric.FirstLaunch
            })
        });

        return (game, definitionId);
    }

    /// <summary>Builds the sync service against a recording relay.</summary>
    private static IRelaySyncService BuildSync(TestAppHost host, IRelayApiClient api) =>
        new RelaySyncService(
            api,
            host.Resolve<ICatalogService>(),
            host.Resolve<IAchievementRepository>(),
            NullLogger<RelaySyncService>.Instance);

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort.
        }
    }

    /// <summary>A relay that records what it was sent and can be taken offline.</summary>
    private sealed class RecordingRelayApi : IRelayApiClient
    {
        private readonly Dictionary<string, string> _assigned = new(StringComparer.Ordinal);

        public bool IsOffline { get; set; }

        public List<AchievementUnlockDto> PushedUnlocks { get; } = [];

        public bool IsConfigured => true;

        public Task<RelayInfo> GetRelayInfoAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            return Task.FromResult(new RelayInfo { RelayId = "rly_test", SchemaVersion = 1 });
        }

        public Task<RegisterResponse> RegisterAsync(string displayName, CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();

            return Task.FromResult(new RegisterResponse
            {
                FriendCode = "GL-AAAAA-BBBBB",
                AuthToken = "glr_test",
                DeviceId = "device"
            });
        }

        public Task<FriendListResponse> GetFriendsAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            return Task.FromResult(new FriendListResponse());
        }

        public Task<CatalogResolveResponse> ResolveCatalogAsync(
            CatalogResolveRequest request,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();

            if (!_assigned.TryGetValue(request.Fingerprint, out var id))
            {
                id = "app_" + Guid.NewGuid().ToString("N");
                _assigned[request.Fingerprint] = id;
            }

            return Task.FromResult(new CatalogResolveResponse
            {
                CatalogId = id,
                CanonicalTitle = request.Title,
                WasCreated = true
            });
        }

        public Task<AchievementSyncResponse> SyncAchievementsAsync(
            AchievementSyncRequest request,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            PushedUnlocks.AddRange(request.Unlocks);

            return Task.FromResult(new AchievementSyncResponse
            {
                Accepted = request.Unlocks.Count,
                Unlocks = request.Unlocks
            });
        }

        private void ThrowIfOffline()
        {
            if (IsOffline)
            {
                throw new RelayApiException("The relay could not be reached.", isTransient: true);
            }
        }
    }
}
