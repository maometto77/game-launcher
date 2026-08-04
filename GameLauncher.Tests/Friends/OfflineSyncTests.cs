using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Friends;
using GameLauncher.Shared.Contracts;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Friends;

/// <summary>
/// Covers the offline-to-online transition: work done while the relay was
/// unreachable is queued locally and pushed when it returns.
/// </summary>
/// <remarks>
/// Driven through a substituted <see cref="IRelayApiClient"/> rather than a real
/// server, because the behaviour under test is what the launcher does when calls
/// <em>fail</em> — and a fake makes failing deterministic rather than a matter of
/// timing.
/// </remarks>
public sealed class OfflineSyncTests
{
    [Fact]
    public async Task Nothing_is_pushed_while_the_relay_is_unreachable()
    {
        using var host = new TestAppHost();
        var api = new FakeRelayApiClient { IsOffline = true };

        await SeedUnlockAsync(host);

        var sync = BuildSyncService(host, api);
        var result = await sync.SynchronizeAsync();

        Assert.False(result.Completed);
        Assert.Equal(0, result.CatalogEntriesPromoted);
        Assert.Equal(0, result.UnlocksPushed);

        // Still queued, and the launcher carried on regardless.
        Assert.NotEmpty(await host.Resolve<ICatalogService>().GetPendingRegistrationsAsync());
    }

    [Fact]
    public async Task Coming_online_promotes_the_catalog_and_pushes_queued_unlocks()
    {
        using var host = new TestAppHost();
        var api = new FakeRelayApiClient { IsOffline = true };

        var (catalogId, definitionId) = await SeedUnlockAsync(host);

        var sync = BuildSyncService(host, api);

        // Offline: the unlock is earned and queued, and goes nowhere.
        await sync.SynchronizeAsync();
        Assert.Empty(api.PushedUnlocks);

        // The relay comes back.
        api.IsOffline = false;

        var result = await sync.SynchronizeAsync();

        Assert.True(result.Completed);
        Assert.Equal(1, result.CatalogEntriesPromoted);

        // The provisional identity was replaced by the assigned one, and the
        // unlock went out under the assigned id rather than the local one.
        var pushed = Assert.Single(api.PushedUnlocks);
        Assert.StartsWith("app_", pushed.CatalogId, StringComparison.Ordinal);
        Assert.Equal("ACH_FINISH", pushed.ApiName);
        Assert.DoesNotContain("local:", pushed.CatalogId, StringComparison.Ordinal);

        // Stamped, so it is no longer queued.
        var achievements = host.Resolve<IAchievementRepository>();
        Assert.Empty(await achievements.GetUnsyncedUnlocksAsync(50));

        // The catalog entry is no longer provisional.
        Assert.Empty(await host.Resolve<ICatalogService>().GetPendingRegistrationsAsync());
        Assert.NotEqual(catalogId, api.LastAssignedCatalogId);
        Assert.True(definitionId > 0);
    }

    [Fact]
    public async Task Replaying_a_completed_pass_pushes_nothing_further()
    {
        using var host = new TestAppHost();
        var api = new FakeRelayApiClient();

        await SeedUnlockAsync(host);

        var sync = BuildSyncService(host, api);

        await sync.SynchronizeAsync();
        var afterFirst = api.PushedUnlocks.Count;

        var second = await sync.SynchronizeAsync();

        // The queue is empty, so a second pass is a no-op. Without stamping on a
        // successful response, this would resend the same unlock forever.
        Assert.Equal(afterFirst, api.PushedUnlocks.Count);
        Assert.False(second.DidWork);
    }

    [Fact]
    public async Task A_provisional_entry_never_leaks_into_an_unlock_push()
    {
        using var host = new TestAppHost();
        var api = new FakeRelayApiClient();

        await SeedUnlockAsync(host);

        // Queried before any sync: while the entry is provisional the unlock must
        // not be eligible, because the relay has never heard of a 'local:' id and
        // would reject it on every attempt, blocking everything behind it.
        var pending = await host.Resolve<IAchievementRepository>().GetUnsyncedUnlocksAsync(50);
        Assert.Empty(pending);

        await BuildSyncService(host, api).SynchronizeAsync();

        // After promotion it becomes eligible and goes out.
        Assert.Single(api.PushedUnlocks);
    }

    /// <summary>Creates a game with a provisional catalog entry and one earned achievement.</summary>
    private static async Task<(string CatalogId, int DefinitionId)> SeedUnlockAsync(TestAppHost host)
    {
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

        var definitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_FINISH",
            Title = "Finish the game",
            Kind = AchievementKind.Meta
        });

        await achievements.UnlockAsync(definitionId, DateTimeOffset.Now.AddHours(-2));

        return (entry.CatalogId, definitionId);
    }

    /// <summary>Builds the sync service against a substituted relay client.</summary>
    private static IRelaySyncService BuildSyncService(TestAppHost host, IRelayApiClient api) =>
        new RelaySyncService(
            api,
            host.Resolve<ICatalogService>(),
            host.Resolve<IAchievementRepository>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RelaySyncService>.Instance);

    /// <summary>
    /// A relay client that can be switched between reachable and unreachable.
    /// </summary>
    private sealed class FakeRelayApiClient : IRelayApiClient
    {
        private readonly Dictionary<string, string> _assignedByFingerprint = new(StringComparer.Ordinal);

        public bool IsOffline { get; set; }

        public List<AchievementUnlockDto> PushedUnlocks { get; } = [];

        public string? LastAssignedCatalogId { get; private set; }

        public bool IsConfigured => true;

        /// <summary>The identity this fake relay reports. Changing it simulates switching relay.</summary>
        public string RelayId { get; set; } = "rly_fake";

        public Task<RelayInfo> GetRelayInfoAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();
            return Task.FromResult(new RelayInfo { RelayId = RelayId, Name = "Fake relay", SchemaVersion = 1 });
        }

        public Task<RegisterResponse> RegisterAsync(string displayName, CancellationToken cancellationToken = default)
        {
            ThrowIfOffline();

            return Task.FromResult(new RegisterResponse
            {
                FriendCode = "GL-AAAAA-BBBBB",
                AuthToken = "glr_fake",
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

            // Stable per fingerprint, mirroring the real relay: resolving the same
            // fingerprint twice must return the same identity.
            if (!_assignedByFingerprint.TryGetValue(request.Fingerprint, out var assigned))
            {
                assigned = "app_" + Guid.NewGuid().ToString("N");
                _assignedByFingerprint[request.Fingerprint] = assigned;
            }

            LastAssignedCatalogId = assigned;

            return Task.FromResult(new CatalogResolveResponse
            {
                CatalogId = assigned,
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
