using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Friends;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Shared.Contracts;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Friends;

/// <summary>
/// Covers pointing the launcher at a different relay.
/// </summary>
/// <remarks>
/// A catalog id only means something within the relay that issued it. Carrying
/// one across to another relay would attach this user's achievements to whatever
/// unrelated title happens to hold that id there, so the migration has to reset
/// them — while losing none of the local library it is attached to.
/// </remarks>
public sealed class RelayMigrationTests
{
    private const string RelayA = "rly_aaaaaaaa";
    private const string RelayB = "rly_bbbbbbbb";

    [Fact]
    public async Task First_contact_registers_and_records_the_relay()
    {
        using var host = new TestAppHost();
        var api = new StubRelayApi { RelayId = RelayA };

        await ConfigureRelayAsync(host, "http://relay-a.example");

        var result = await BuildIdentityService(host, api).EstablishAsync();

        Assert.True(result.IsReady);
        Assert.Equal(RelayA, result.RelayId);
        Assert.False(result.RelayChanged);

        var settings = host.Resolve<ISettingsService>().Current;
        Assert.Equal(RelayA, settings.ActiveRelayId);
        Assert.True(settings.IsRegistered);
    }

    [Fact]
    public async Task Switching_relay_demotes_assigned_ids_but_keeps_every_local_record()
    {
        using var host = new TestAppHost();
        var api = new StubRelayApi { RelayId = RelayA };

        await ConfigureRelayAsync(host, "http://relay-a.example");

        var identityService = BuildIdentityService(host, api);
        await identityService.EstablishAsync();

        // A game, an achievement, an unlock and a collection, all under an
        // identity assigned by relay A.
        var seeded = await SeedAssignedGameAsync(host, RelayA);

        var beforeCatalogId = seeded.CatalogId;
        Assert.StartsWith("app_", beforeCatalogId, StringComparison.Ordinal);

        // The user points the launcher at a different relay.
        api.RelayId = RelayB;
        var result = await identityService.EstablishAsync();

        Assert.True(result.RelayChanged);
        Assert.Equal(1, result.EntriesMarkedForReResolution);

        // Nothing local was lost.
        var games = host.Resolve<IGameRepository>();
        var achievements = host.Resolve<IAchievementRepository>();
        var collections = host.Resolve<ICollectionRepository>();
        var sessions = host.Resolve<IPlaySessionRepository>();

        var game = await games.GetByIdAsync(seeded.GameId);
        Assert.NotNull(game);
        Assert.Equal("Hollow Signal", game!.Title);
        Assert.Single(await collections.GetAllAsync());
        Assert.Equal(1, await sessions.CountForGameAsync(seeded.GameId));

        // The game now points at a provisional identity, not relay A's.
        Assert.NotNull(game.CatalogId);
        Assert.StartsWith(CatalogEntry.ProvisionalPrefix, game.CatalogId!, StringComparison.Ordinal);
        Assert.NotEqual(beforeCatalogId, game.CatalogId);

        // The achievement definition followed the identity, and the unlock is intact.
        var definitions = await achievements.GetDefinitionsForCatalogAsync(game.CatalogId!);
        Assert.Single(definitions);
        Assert.Contains(definitions[0].Id, await achievements.GetUnlockedDefinitionIdsAsync());

        // It is queued for re-resolution against the new relay.
        Assert.Single(await host.Resolve<ICatalogService>().GetPendingRegistrationsAsync());
    }

    [Fact]
    public async Task Switching_relay_requeues_history_the_new_relay_has_never_seen()
    {
        using var host = new TestAppHost();
        var api = new StubRelayApi { RelayId = RelayA };

        await ConfigureRelayAsync(host, "http://relay-a.example");
        var identityService = BuildIdentityService(host, api);
        await identityService.EstablishAsync();

        var seeded = await SeedAssignedGameAsync(host, RelayA);

        var achievements = host.Resolve<IAchievementRepository>();
        var sessions = host.Resolve<IPlaySessionRepository>();

        // Pretend everything was already pushed to relay A.
        await achievements.MarkUnlocksSyncedAsync([seeded.DefinitionId], DateTimeOffset.Now);
        var completed = await sessions.GetUnsyncedAsync(10);
        await sessions.MarkSyncedAsync(completed.Select(s => s.SessionKey).ToArray(), DateTimeOffset.Now);
        Assert.Empty(await sessions.GetUnsyncedAsync(10));

        api.RelayId = RelayB;
        await identityService.EstablishAsync();

        // Relay B has seen none of it. A watermark recorded against relay A would
        // silently withhold everything earned so far.
        Assert.NotEmpty(await sessions.GetUnsyncedAsync(10));

        // The unlock is re-queued too, though it only becomes eligible to push
        // once its catalog entry has been re-resolved.
        var stillProvisional = await achievements.GetUnsyncedUnlocksAsync(10);
        Assert.Empty(stillProvisional);
    }

    [Fact]
    public async Task Credentials_are_kept_per_relay_and_restored_on_switching_back()
    {
        using var host = new TestAppHost();
        var api = new StubRelayApi { RelayId = RelayA };
        var settingsService = host.Resolve<ISettingsService>();

        await ConfigureRelayAsync(host, "http://relay-a.example");
        var identityService = BuildIdentityService(host, api);

        await identityService.EstablishAsync();
        var codeOnA = settingsService.Current.EffectiveFriendCode;

        api.RelayId = RelayB;
        await identityService.EstablishAsync();
        var codeOnB = settingsService.Current.EffectiveFriendCode;

        // Each relay issues its own identity; they must not overwrite each other.
        Assert.NotEqual(codeOnA, codeOnB);

        api.RelayId = RelayA;
        await identityService.EstablishAsync();

        // Going back restores the original identity rather than registering again,
        // which is what keeps the friendships built up on relay A.
        Assert.Equal(codeOnA, settingsService.Current.EffectiveFriendCode);
        Assert.Equal(2, settingsService.Current.RelayIdentities.Count);
    }

    [Fact]
    public async Task Establishing_repeatedly_against_the_same_relay_changes_nothing()
    {
        using var host = new TestAppHost();
        var api = new StubRelayApi { RelayId = RelayA };

        await ConfigureRelayAsync(host, "http://relay-a.example");
        var identityService = BuildIdentityService(host, api);

        await identityService.EstablishAsync();
        await SeedAssignedGameAsync(host, RelayA);

        // Idempotence matters because this runs on every launch and every
        // reconnect. A second pass must not demote what the first left alone.
        for (var pass = 0; pass < 3; pass++)
        {
            var result = await identityService.EstablishAsync();

            Assert.False(result.RelayChanged);
            Assert.Equal(0, result.EntriesMarkedForReResolution);
        }

        Assert.Empty(await host.Resolve<ICatalogService>().GetPendingRegistrationsAsync());
    }

    [Fact]
    public async Task An_unreachable_relay_changes_nothing_at_all()
    {
        using var host = new TestAppHost();
        var api = new StubRelayApi { RelayId = RelayA };

        await ConfigureRelayAsync(host, "http://relay-a.example");
        var identityService = BuildIdentityService(host, api);

        await identityService.EstablishAsync();
        var seeded = await SeedAssignedGameAsync(host, RelayA);

        // Offline. Migration must never happen on a guess: the launcher cannot
        // tell "different relay" from "no answer", and demoting on the latter
        // would churn identities every time the network hiccupped.
        api.IsOffline = true;
        var result = await identityService.EstablishAsync();

        Assert.False(result.IsReady);
        Assert.False(result.RelayChanged);
        Assert.Equal(0, result.EntriesMarkedForReResolution);

        var game = await host.Resolve<IGameRepository>().GetByIdAsync(seeded.GameId);
        Assert.Equal(seeded.CatalogId, game!.CatalogId);
        Assert.Equal(RelayA, host.Resolve<ISettingsService>().Current.ActiveRelayId);
    }

    /// <summary>Points the launcher at a relay address.</summary>
    private static async Task ConfigureRelayAsync(TestAppHost host, string url)
    {
        var settings = host.Resolve<ISettingsService>();
        await settings.LoadAsync();
        await settings.SaveAsync(settings.Current with { RelayUrl = url });
    }

    /// <summary>
    /// Creates a game whose catalog entry has been promoted to an id assigned by
    /// the named relay, with an achievement, an unlock, a collection and a
    /// completed play session.
    /// </summary>
    private static async Task<(int GameId, string CatalogId, int DefinitionId)> SeedAssignedGameAsync(
        TestAppHost host,
        string relayId)
    {
        var catalog = host.Resolve<ICatalogService>();
        var catalogRepository = host.Resolve<ICatalogRepository>();
        var games = host.Resolve<IGameRepository>();
        var achievements = host.Resolve<IAchievementRepository>();
        var collections = host.Resolve<ICollectionRepository>();
        var sessions = host.Resolve<IPlaySessionRepository>();

        var provisional = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        var assignedId = "app_" + Guid.NewGuid().ToString("N");
        await catalogRepository.PromoteAsync(provisional.CatalogId, assignedId, relayId, "Hollow Signal");

        var collectionId = await collections.AddAsync(new Collection
        {
            Name = "Favourites",
            DateAdded = DateTimeOffset.Now
        });

        var game = new Game
        {
            Title = "Hollow Signal",
            CatalogId = assignedId,
            CollectionId = collectionId,
            ExecutablePath = @"C:\Games\Hollow\game.exe",
            DateAdded = DateTimeOffset.Now,
            Tags = ["Horror"]
        };

        await games.AddAsync(game);

        var definitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = assignedId,
            ApiName = "ACH_FINISH",
            Title = "Finish the game",
            Kind = AchievementKind.Meta
        });

        await achievements.UnlockAsync(definitionId, DateTimeOffset.Now.AddDays(-1));

        var sessionId = await sessions.StartAsync(game.Id, DateTimeOffset.Now.AddHours(-2), "device-1");
        await sessions.CompleteAsync(sessionId, DateTimeOffset.Now.AddHours(-1), 3600);

        return (game.Id, assignedId, definitionId);
    }

    /// <summary>Builds the identity service against a stub relay.</summary>
    private static IRelayIdentityService BuildIdentityService(TestAppHost host, IRelayApiClient api) =>
        new RelayIdentityService(
            api,
            host.Resolve<ISettingsService>(),
            host.Resolve<ICatalogRepository>(),
            host.Resolve<IAchievementRepository>(),
            host.Resolve<IPlaySessionRepository>(),
            host.Resolve<IFriendCacheRepository>(),
            NullLogger<RelayIdentityService>.Instance);

    /// <summary>A relay whose reported identity can be switched at will.</summary>
    private sealed class StubRelayApi : IRelayApiClient
    {
        private int _registrations;

        public string RelayId { get; set; } = RelayA;

        public bool IsOffline { get; set; }

        public bool IsConfigured => true;

        public Task<RelayInfo> GetRelayInfoAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            return Task.FromResult(new RelayInfo { RelayId = RelayId, SchemaVersion = 1 });
        }

        public Task<RegisterResponse> RegisterAsync(string displayName, CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();

            // A distinct identity per registration, as a real relay would issue.
            var index = ++_registrations;

            return Task.FromResult(new RegisterResponse
            {
                FriendCode = $"GL-AAAAA-{index:D5}",
                AuthToken = $"glr_token_{index}",
                DeviceId = $"device_{index}"
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

            return Task.FromResult(new CatalogResolveResponse
            {
                CatalogId = "app_" + Guid.NewGuid().ToString("N"),
                CanonicalTitle = request.Title,
                WasCreated = true
            });
        }

        public Task<AchievementSyncResponse> SyncAchievementsAsync(
            AchievementSyncRequest request,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            return Task.FromResult(new AchievementSyncResponse { Accepted = 0, Unlocks = [] });
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
