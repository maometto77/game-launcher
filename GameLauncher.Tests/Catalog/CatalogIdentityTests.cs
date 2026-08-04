using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Catalog;

/// <summary>
/// Validates the shared catalog identity design end to end against a real
/// database.
/// </summary>
/// <remarks>
/// The promotion step rewrites a primary key and relies on
/// <c>ON UPDATE CASCADE</c> to carry every reference with it. That is the load
/// bearing claim of the whole design, and getting it wrong would either strand
/// achievements on a dead identity or — worse — cascade unlock rows out of
/// existence. These tests exercise it without needing a relay.
/// </remarks>
public sealed class CatalogIdentityTests
{
    [Fact]
    public void Fingerprint_is_stable_across_install_locations()
    {
        using var host = new TestAppHost();
        var catalog = host.Resolve<ICatalogService>();

        // Same publisher metadata, different drives and file sizes: two people who
        // installed the same game must land on one catalog entry.
        var first = catalog.ComputeFingerprint("Hollow Signal", Executable(@"C:\Games\Hollow\game.exe", 4_000_000));
        var second = catalog.ComputeFingerprint("hollow signal", Executable(@"D:\Steam\Hollow\game.exe", 9_000_000));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Fingerprint_differs_between_titles()
    {
        using var host = new TestAppHost();
        var catalog = host.Resolve<ICatalogService>();

        var first = catalog.ComputeFingerprint("Hollow Signal", Executable(@"C:\Games\A\game.exe", 1));
        var second = catalog.ComputeFingerprint("Aurora Drift", Executable(@"C:\Games\B\other.exe", 1));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Second_copy_of_a_known_game_reuses_its_catalog_entry()
    {
        using var host = new TestAppHost();
        var catalog = host.Resolve<ICatalogService>();

        var executable = Executable(@"C:\Games\Hollow\game.exe", 4_000_000);

        var first = await catalog.EnsureEntryAsync("Hollow Signal", executable);
        var second = await catalog.EnsureEntryAsync("Hollow Signal", executable);

        Assert.Equal(first.CatalogId, second.CatalogId);
    }

    [Fact]
    public async Task Promotion_carries_games_and_achievements_and_preserves_unlocks()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var games = host.Resolve<IGameRepository>();
        var achievements = host.Resolve<IAchievementRepository>();

        var entry = await catalog.EnsureEntryAsync("Hollow Signal", Executable(@"C:\Games\Hollow\game.exe", 1));
        Assert.True(entry.IsProvisional);

        var game = NewGame("Hollow Signal", entry.CatalogId, @"C:\Games\Hollow\game.exe");
        await games.AddAsync(game);

        var definitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_FINISH",
            Title = "Finish the game",
            Kind = AchievementKind.Meta
        });

        Assert.True(await achievements.UnlockAsync(definitionId, DateTimeOffset.Now));

        // The relay assigns a real identity.
        const string assigned = "app-1042";
        var surviving = await catalog.ApplyAssignedIdentityAsync(
            entry.CatalogId, assigned, source: "relay.example", canonicalTitle: "Hollow Signal");

        Assert.Equal(assigned, surviving);

        // The game followed the rewritten key without being touched directly.
        var reloadedGame = await games.GetByIdAsync(game.Id);
        Assert.NotNull(reloadedGame);
        Assert.Equal(assigned, reloadedGame!.CatalogId);

        // So did the achievement definition.
        var definitions = await achievements.GetDefinitionsForCatalogAsync(assigned);
        Assert.Single(definitions);
        Assert.Equal("ACH_FINISH", definitions[0].ApiName);

        // And the unlock survived. This is the assertion that matters most:
        // rebuilding the table instead of rewriting the key would have cascaded
        // this row away silently.
        var unlocked = await achievements.GetUnlockedDefinitionIdsAsync();
        Assert.Contains(definitions[0].Id, unlocked);

        var promotedEntry = await host.Resolve<ICatalogRepository>().GetByIdAsync(assigned);
        Assert.NotNull(promotedEntry);
        Assert.False(promotedEntry!.IsProvisional);
        Assert.Equal("relay.example", promotedEntry.Source);
    }

    [Fact]
    public async Task Promotion_onto_an_existing_identity_merges_instead_of_failing()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var catalogRepository = host.Resolve<ICatalogRepository>();
        var games = host.Resolve<IGameRepository>();
        var achievements = host.Resolve<IAchievementRepository>();

        // Two separately-added copies that the local fingerprint did not unify —
        // different publisher metadata, same underlying title.
        var first = await catalog.EnsureEntryAsync("Hollow Signal", Executable(@"C:\A\game.exe", 1, "Hollow Signal"));
        var second = await catalog.EnsureEntryAsync("Hollow Signal HD", Executable(@"C:\B\hd.exe", 1, "Hollow Signal HD"));

        Assert.NotEqual(first.CatalogId, second.CatalogId);

        var gameA = NewGame("Hollow Signal", first.CatalogId, @"C:\A\game.exe");
        var gameB = NewGame("Hollow Signal HD", second.CatalogId, @"C:\B\hd.exe");
        await games.AddAsync(gameA);
        await games.AddAsync(gameB);

        // Both entries carry an achievement with the same api name, which the
        // unique index would reject if the merge repointed both.
        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = first.CatalogId, ApiName = "ACH_FINISH", Title = "Finish", Kind = AchievementKind.Meta
        });

        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = second.CatalogId, ApiName = "ACH_FINISH", Title = "Finish", Kind = AchievementKind.Meta
        });

        const string assigned = "app-2001";

        // The relay resolves the first to a real id, then tells us the second is
        // the very same title.
        await catalog.ApplyAssignedIdentityAsync(first.CatalogId, assigned, "relay.example", "Hollow Signal");
        var surviving = await catalog.ApplyAssignedIdentityAsync(
            second.CatalogId, assigned, "relay.example", "Hollow Signal");

        Assert.Equal(assigned, surviving);

        // Both installations now point at one shared identity.
        var reloadedA = await games.GetByIdAsync(gameA.Id);
        var reloadedB = await games.GetByIdAsync(gameB.Id);
        Assert.Equal(assigned, reloadedA!.CatalogId);
        Assert.Equal(assigned, reloadedB!.CatalogId);

        // The duplicate api name was folded in rather than violating the index.
        var definitions = await achievements.GetDefinitionsForCatalogAsync(assigned);
        Assert.Single(definitions);

        // The absorbed entry is KEPT as a redirect. An assigned identity is
        // immutable and may still be held by another client or by the relay, so
        // it must resolve rather than vanish.
        var absorbed = await catalogRepository.GetByIdAsync(second.CatalogId);
        Assert.NotNull(absorbed);
        Assert.Equal(assigned, absorbed!.SupersededByCatalogId);

        var resolved = await catalogRepository.ResolveCanonicalAsync(second.CatalogId);
        Assert.Equal(assigned, resolved!.CatalogId);

        Assert.Empty(await catalogRepository.GetProvisionalAsync());
    }

    [Fact]
    public async Task Merge_preserves_an_unlock_only_the_absorbed_entry_had()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var catalogRepository = host.Resolve<ICatalogRepository>();
        var achievements = host.Resolve<IAchievementRepository>();

        var keep = await catalog.EnsureEntryAsync("Keep", Executable(@"C:\A\a.exe", 1, "Keep"));
        var absorb = await catalog.EnsureEntryAsync("Absorb", Executable(@"C:\B\b.exe", 1, "Absorb"));

        // Both define ACH_FINISH, but only the entry about to be absorbed has it
        // unlocked. Naively deleting the duplicate would cascade the unlock away.
        var keptDefinitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = keep.CatalogId, ApiName = "ACH_FINISH", Title = "Finish", Kind = AchievementKind.Meta
        });

        var absorbedDefinitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = absorb.CatalogId, ApiName = "ACH_FINISH", Title = "Finish", Kind = AchievementKind.Meta
        });

        var earnedAt = DateTimeOffset.Now.AddDays(-3);
        Assert.True(await achievements.UnlockAsync(absorbedDefinitionId, earnedAt));

        await catalogRepository.MergeIntoAsync(absorb.CatalogId, keep.CatalogId);

        // The survivor inherited the unlock rather than losing it.
        var unlocked = await achievements.GetUnlockedDefinitionIdsAsync();
        Assert.Contains(keptDefinitionId, unlocked);

        var unlocks = await achievements.GetUnlocksAsync();
        var carried = unlocks.Single(unlock => unlock.DefinitionId == keptDefinitionId);
        Assert.Equal(earnedAt.ToUnixTimeSeconds(), carried.UnlockedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Merge_keeps_the_earlier_of_two_unlock_times()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var catalogRepository = host.Resolve<ICatalogRepository>();
        var achievements = host.Resolve<IAchievementRepository>();

        var keep = await catalog.EnsureEntryAsync("Keep", Executable(@"C:\A\a.exe", 1, "Keep"));
        var absorb = await catalog.EnsureEntryAsync("Absorb", Executable(@"C:\B\b.exe", 1, "Absorb"));

        var keptId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = keep.CatalogId, ApiName = "ACH_FINISH", Title = "Finish", Kind = AchievementKind.Meta
        });

        var absorbedId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = absorb.CatalogId, ApiName = "ACH_FINISH", Title = "Finish", Kind = AchievementKind.Meta
        });

        var later = DateTimeOffset.Now.AddDays(-1);
        var earlier = DateTimeOffset.Now.AddDays(-10);

        await achievements.UnlockAsync(keptId, later);
        await achievements.UnlockAsync(absorbedId, earlier);

        await catalogRepository.MergeIntoAsync(absorb.CatalogId, keep.CatalogId);

        // The user earned it ten days ago; housekeeping must not move that forward.
        var unlocks = await achievements.GetUnlocksAsync();
        var carried = unlocks.Single(unlock => unlock.DefinitionId == keptId);
        Assert.Equal(earlier.ToUnixTimeSeconds(), carried.UnlockedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Alias_lets_a_second_fingerprint_resolve_to_the_same_title()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();

        var entry = await catalog.EnsureEntryAsync("Hollow Signal", Executable(@"C:\A\game.exe", 1));

        // A launcher executable shipped alongside the game produces a different
        // fingerprint for what is really the same title.
        var launcherFingerprint = catalog.ComputeFingerprint(
            "Hollow Signal Launcher", Executable(@"C:\A\launcher.exe", 1, "Hollow Signal Launcher"));

        Assert.True(await catalog.RegisterAliasAsync(entry.CatalogId, launcherFingerprint, "relay.example"));

        var viaAlias = await catalog.EnsureEntryAsync(
            "Hollow Signal Launcher", Executable(@"C:\A\launcher.exe", 1, "Hollow Signal Launcher"));

        Assert.Equal(entry.CatalogId, viaAlias.CatalogId);
    }

    [Fact]
    public async Task Alias_already_bound_elsewhere_is_not_silently_rebound()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();

        var first = await catalog.EnsureEntryAsync("First", Executable(@"C:\A\a.exe", 1, "First"));
        var second = await catalog.EnsureEntryAsync("Second", Executable(@"C:\B\b.exe", 1, "Second"));

        var firstFingerprint = catalog.ComputeFingerprint("First", Executable(@"C:\A\a.exe", 1, "First"));

        // Rebinding a fingerprint is a merge decision, not something a stray
        // observation should be able to do.
        Assert.False(await catalog.RegisterAliasAsync(second.CatalogId, firstFingerprint, "relay.example"));

        var resolved = await catalog.EnsureEntryAsync("First", Executable(@"C:\A\a.exe", 1, "First"));
        Assert.Equal(first.CatalogId, resolved.CatalogId);
    }

    [Fact]
    public async Task Removing_a_game_leaves_its_achievements_intact()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var games = host.Resolve<IGameRepository>();
        var achievements = host.Resolve<IAchievementRepository>();

        var entry = await catalog.EnsureEntryAsync("Hollow Signal", Executable(@"C:\Games\Hollow\game.exe", 1));
        var game = NewGame("Hollow Signal", entry.CatalogId, @"C:\Games\Hollow\game.exe");
        await games.AddAsync(game);

        var definitionId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId, ApiName = "ACH_FINISH", Title = "Finish", Kind = AchievementKind.Meta
        });

        await achievements.UnlockAsync(definitionId, DateTimeOffset.Now);

        Assert.True(await games.DeleteAsync(game.Id));

        // Uninstalling must not erase what was earned. Before schema v3 the
        // definition cascaded from the game row and this would have been empty.
        var definitions = await achievements.GetDefinitionsForCatalogAsync(entry.CatalogId);
        Assert.Single(definitions);
        Assert.Contains(definitionId, await achievements.GetUnlockedDefinitionIdsAsync());
    }

    [Fact]
    public async Task Entries_left_without_a_fingerprint_are_repaired_and_become_matchable()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var catalogRepository = host.Resolve<ICatalogRepository>();
        var games = host.Resolve<IGameRepository>();

        // Reproduces what the schema v3 SQL backfill produced: an entry with no
        // fingerprint, and therefore no alias. Such an entry can never be matched,
        // so re-adding the same game would create a second entry for one title.
        var now = DateTimeOffset.Now;
        var entry = new CatalogEntry
        {
            CatalogId = CatalogEntry.ProvisionalPrefix + Guid.NewGuid().ToString("N"),
            Source = CatalogEntry.LocalSource,
            IsProvisional = true,
            CanonicalTitle = "Legacy Game",
            MatchFingerprint = string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        };

        await catalogRepository.AddAsync(entry);
        await games.AddAsync(NewGame("Legacy Game", entry.CatalogId, @"C:\Games\Legacy\legacy.exe"));

        Assert.Empty(await catalogRepository.GetAliasesAsync(entry.CatalogId));

        var repaired = await catalog.RepairMissingFingerprintsAsync();
        Assert.Equal(1, repaired);

        // It now has an alias and resolves by fingerprint like any other entry.
        Assert.Single(await catalogRepository.GetAliasesAsync(entry.CatalogId));

        var matched = await catalog.EnsureEntryAsync("Legacy Game", executable: null);
        Assert.Equal(entry.CatalogId, matched.CatalogId);

        // Idempotent: a second pass finds nothing left to do.
        Assert.Equal(0, await catalog.RepairMissingFingerprintsAsync());
    }

    private static Game NewGame(string title, string catalogId, string executablePath) => new()
    {
        Title = title,
        CatalogId = catalogId,
        ExecutablePath = executablePath,
        InstallDir = Path.GetDirectoryName(executablePath),
        DateAdded = DateTimeOffset.Now,
        Tags = []
    };

    private static ExecutableInfo Executable(string path, long size, string product = "Hollow Signal") => new(
        Path: path,
        FileName: Path.GetFileName(path),
        SuggestedTitle: product,
        ProductName: product,
        FileDescription: product,
        CompanyName: "Sample Studio",
        FileVersion: "1.0.0.0",
        FileSizeBytes: size,
        Architecture: ExecutableArchitecture.X64,
        Subsystem: ExecutableSubsystem.WindowsGui,
        IsValidExecutable: true);
}
