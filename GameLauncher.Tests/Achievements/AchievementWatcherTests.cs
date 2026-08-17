using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements.Emulators;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Achievements;

/// <summary>
/// Covers the local achievement pipeline: reading the files Steam emulators
/// write, storing what they say, and announcing only what is new.
/// </summary>
/// <remarks>
/// The parser is pure, so it is tested against captured file contents. These
/// formats are conventions rather than specifications — there is no document to
/// check, only what the writers actually produce — which is exactly why every
/// shape they emit is pinned here.
/// </remarks>
public sealed class AchievementWatcherTests
{
    private const string GoldbergJson =
        """
        {
          "ACH_WIN_ONE_GAME":  { "earned": true,  "earned_time": 1700000000 },
          "ACH_WIN_100_GAMES": { "earned": false, "earned_time": 0 },
          "ACH_TRAVEL_FAR":    { "earned": false, "earned_time": 0, "progress": 250, "max_progress": 1000 }
        }
        """;

    private const string CodexIni =
        """
        [ACH_WIN_ONE_GAME]
        Achieved=1
        UnlockTime=1700000000

        [ACH_WIN_100_GAMES]
        Achieved=0
        UnlockTime=0
        """;

    [Fact]
    public void Goldbergs_json_yields_unlocks_and_progress()
    {
        var snapshot = EmulatorAchievementParser.Parse(GoldbergJson, 480, "goldberg", "achievements.json");

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Equal(1, snapshot.UnlockedCount);

        var earned = snapshot.Entries.Single(entry => entry.ApiName == "ACH_WIN_ONE_GAME");

        Assert.True(earned.IsUnlocked);
        Assert.Equal(480, earned.SteamAppId);
        Assert.Equal("goldberg", earned.SourceKey);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1700000000).ToLocalTime(), earned.UnlockedAt);

        var partial = snapshot.Entries.Single(entry => entry.ApiName == "ACH_TRAVEL_FAR");

        Assert.False(partial.IsUnlocked);
        Assert.Equal(0.25, partial.Fraction);
    }

    [Fact]
    public void A_locked_achievement_never_carries_a_time()
    {
        // These writers store 0 for "never". Read as a Unix timestamp it would be
        // 1970, and the interface would claim the achievement was earned then.
        var snapshot = EmulatorAchievementParser.Parse(GoldbergJson, 480, "goldberg", "achievements.json");

        Assert.All(
            snapshot.Entries.Where(entry => !entry.IsUnlocked),
            entry => Assert.Null(entry.UnlockedAt));
    }

    [Fact]
    public void The_codex_and_rune_section_form_is_read()
    {
        var snapshot = EmulatorAchievementParser.Parse(CodexIni, 271590, "codex", "achievements.ini");

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Equal(1, snapshot.UnlockedCount);
        Assert.Equal(271590, snapshot.SteamAppId);
    }

    [Fact]
    public void A_flat_achievement_list_is_read_too()
    {
        // The other dialect, from the same family of writers, in a file that
        // announces itself no differently.
        var snapshot = EmulatorAchievementParser.Parse(
            """
            [Achievements]
            ACH_FIRST_BLOOD=1
            ACH_MARATHON=1700000000
            ACH_NEVER=0
            """,
            730,
            "rune",
            "achievements.ini");

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Equal(2, snapshot.UnlockedCount);

        // A value big enough to be a timestamp is one, and that also makes it
        // earned: nothing writes an unlock time for something never unlocked.
        var marathon = snapshot.Entries.Single(entry => entry.ApiName == "ACH_MARATHON");

        Assert.True(marathon.IsUnlocked);
        Assert.NotNull(marathon.UnlockedAt);
    }

    [Fact]
    public void Statistics_are_kept_but_never_counted_as_unlocked()
    {
        var snapshot = EmulatorAchievementParser.Parse(
            """
            [Stats]
            TOTAL_KILLS=4213
            DISTANCE_WALKED=1500.5
            """,
            480,
            "codex",
            "stats.ini");

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Equal(0, snapshot.UnlockedCount);
        Assert.All(snapshot.Entries, entry =>
            Assert.Equal(ExternalAchievementKind.Statistic, entry.Kind));

        // Invariant parsing: a machine whose locale uses a comma for the decimal
        // point must not read 1500.5 as fifteen thousand.
        Assert.Equal(1500.5, snapshot.Entries.Single(e => e.ApiName == "DISTANCE_WALKED").CurrentValue);
    }

    [Fact]
    public void A_half_written_file_yields_nothing_rather_than_throwing()
    {
        // The common case, not the exceptional one: the watcher fires while the
        // emulator is still writing.
        Assert.Empty(EmulatorAchievementParser.Parse("{ \"ACH\": { \"ear", 480, "goldberg", "x").Entries);
        Assert.Empty(EmulatorAchievementParser.Parse("", 480, "goldberg", "x").Entries);
        Assert.Empty(EmulatorAchievementParser.Parse(null, 480, "goldberg", "x").Entries);
    }

    [Fact]
    public void The_format_is_inferred_from_the_content_not_the_name()
    {
        // At least one writer has shipped JSON in a file called .ini.
        var snapshot = EmulatorAchievementParser.Parse(GoldbergJson, 480, "codex", "achievements.ini");

        Assert.Equal(3, snapshot.Entries.Count);
    }

    [Fact]
    public async Task Only_a_transition_to_unlocked_is_reported()
    {
        // These files are rewritten in full on every save. Announcing every
        // unlocked achievement in one would fire a toast per achievement, every
        // time the game saves.
        using var host = new TestAppHost();
        var repository = host.Resolve<IExternalAchievementRepository>();

        var first = EmulatorAchievementParser.Parse(GoldbergJson, 480, "goldberg", "a.json").Entries;

        var newlyUnlocked = await repository.ApplySnapshotAsync(first, DateTimeOffset.Now);
        Assert.Single(newlyUnlocked);

        var again = EmulatorAchievementParser.Parse(GoldbergJson, 480, "goldberg", "a.json").Entries;

        Assert.Empty(await repository.ApplySnapshotAsync(again, DateTimeOffset.Now));
    }

    [Fact]
    public async Task A_second_unlock_in_the_same_file_is_reported_when_it_appears()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<IExternalAchievementRepository>();

        await repository.ApplySnapshotAsync(
            EmulatorAchievementParser.Parse(GoldbergJson, 480, "goldberg", "a.json").Entries,
            DateTimeOffset.Now);

        var later = EmulatorAchievementParser.Parse(
            """
            {
              "ACH_WIN_ONE_GAME":  { "earned": true, "earned_time": 1700000000 },
              "ACH_WIN_100_GAMES": { "earned": true, "earned_time": 1700000900 }
            }
            """,
            480,
            "goldberg",
            "a.json").Entries;

        var newlyUnlocked = await repository.ApplySnapshotAsync(later, DateTimeOffset.Now);

        Assert.Equal("ACH_WIN_100_GAMES", Assert.Single(newlyUnlocked).ApiName);
    }

    [Fact]
    public async Task An_achievement_is_never_taken_away_again()
    {
        // A restored save, or an emulator rewriting a file it has lost track of,
        // must not un-earn something the user has already been told they got.
        using var host = new TestAppHost();
        var repository = host.Resolve<IExternalAchievementRepository>();

        await repository.ApplySnapshotAsync(
            EmulatorAchievementParser.Parse(GoldbergJson, 480, "goldberg", "a.json").Entries,
            DateTimeOffset.Now);

        await repository.ApplySnapshotAsync(
            EmulatorAchievementParser.Parse(
                """{ "ACH_WIN_ONE_GAME": { "earned": false, "earned_time": 0 } }""",
                480,
                "goldberg",
                "a.json").Entries,
            DateTimeOffset.Now);

        var stored = await repository.GetForAppAsync(480);

        Assert.True(stored.Single(row => row.ApiName == "ACH_WIN_ONE_GAME").IsUnlocked);
    }

    [Fact]
    public async Task Two_emulators_keep_separate_records_for_one_game()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<IExternalAchievementRepository>();

        await repository.ApplySnapshotAsync(
            EmulatorAchievementParser.Parse(CodexIni, 480, "codex", "a.ini").Entries, DateTimeOffset.Now);

        var fromRune = await repository.ApplySnapshotAsync(
            EmulatorAchievementParser.Parse(CodexIni, 480, "rune", "b.ini").Entries, DateTimeOffset.Now);

        // The same game under a second writer is a second record of play, and
        // neither is authoritative over the other.
        Assert.Single(fromRune);
        Assert.Equal(4, (await repository.GetForAppAsync(480)).Count);
    }

    [Fact]
    public async Task The_tally_counts_achievements_and_ignores_statistics()
    {
        using var host = new TestAppHost();
        var repository = host.Resolve<IExternalAchievementRepository>();

        await repository.ApplySnapshotAsync(
            EmulatorAchievementParser.Parse(GoldbergJson, 480, "goldberg", "a.json").Entries,
            DateTimeOffset.Now);

        await repository.ApplySnapshotAsync(
            EmulatorAchievementParser.Parse("[Stats]\nKILLS=10", 480, "goldberg", "s.ini").Entries,
            DateTimeOffset.Now);

        var (unlocked, total) = await repository.GetTallyAsync(480);

        Assert.Equal(1, unlocked);
        Assert.Equal(3, total);
    }

    [Fact]
    public void The_three_known_roots_are_watched_by_default()
    {
        var roots = AchievementWatcherService.DiscoverRoots();

        Assert.Contains(roots, root => root.SourceKey == "goldberg" &&
                                       root.Root.Contains("Goldberg SteamEmu Saves", StringComparison.Ordinal));
        Assert.Contains(roots, root => root.SourceKey == "rune");
        Assert.Contains(roots, root => root.SourceKey == "codex");
    }

    [Fact]
    public void A_configured_root_is_added_rather_than_replacing_the_known_ones()
    {
        var roots = AchievementWatcherService.DiscoverRoots([@"D:\Emu\Saves"]);

        Assert.Equal(4, roots.Count);
        Assert.Contains(roots, root => root.SourceKey == "custom");
        Assert.Contains(roots, root => root.SourceKey == "goldberg");
    }

    [Fact]
    public async Task A_file_dropped_into_a_watched_folder_is_read_end_to_end()
    {
        // The whole pipeline against real files: a root laid out the way an
        // emulator lays one out, scanned, stored, and matched to the library.
        using var temp = new TempDirectory();
        using var host = new TestAppHost();

        var appFolder = Path.Combine(temp.Path, "480");
        Directory.CreateDirectory(appFolder);

        await File.WriteAllTextAsync(Path.Combine(appFolder, "achievements.json"), GoldbergJson);

        var games = host.Resolve<IGameRepository>();

        await games.AddAsync(new Game
        {
            GlobalKey = Guid.NewGuid().ToString("N"),
            Title = "Spacewar",
            SteamAppId = 480,
            ExecutablePath = Path.Combine(temp.Path, "spacewar.exe"),
            Tags = []
        });

        var watcher = new AchievementWatcherService(
            host.Resolve<IExternalAchievementRepository>(),
            games,
            new StubSettings(temp.Path),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AchievementWatcherService>.Instance);

        var announced = new List<ExternalAchievementUnlockedEventArgs>();

        watcher.AchievementUnlocked += (_, e) =>
        {
            lock (announced)
            {
                announced.Add(e);
            }
        };

        // Scanned directly rather than through StartAsync, whose own background
        // pass would otherwise consume the transition this test is asserting on.
        var unlocked = await watcher.ScanAllAsync();

        Assert.Equal(1, unlocked);

        lock (announced)
        {
            var only = Assert.Single(announced);

            Assert.Equal("ACH_WIN_ONE_GAME", only.Achievement.ApiName);

            // Matched to the library by Steam app id, which is the only key these
            // files carry.
            Assert.Equal("Spacewar", only.Game?.Title);
        }

        watcher.Dispose();

        Assert.Equal(3, (await host.Resolve<IExternalAchievementRepository>().GetForAppAsync(480)).Count);
    }

    /// <summary>Settings that add one watch root and nothing else.</summary>
    private sealed class StubSettings(string root) : GameLauncher.Desktop.Services.Settings.ISettingsService
    {
        public AppSettings Current { get; private set; } = new() { AchievementWatchRoots = [root] };

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }
}
