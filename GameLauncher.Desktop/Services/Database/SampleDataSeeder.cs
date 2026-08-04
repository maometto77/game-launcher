using System.Text.Json;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Catalog;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Database;

/// <summary>
/// Default <see cref="ISampleDataSeeder"/>.
/// </summary>
public sealed class SampleDataSeeder : ISampleDataSeeder
{
    private readonly IGameRepository _games;
    private readonly ICollectionRepository _collections;
    private readonly IAchievementRepository _achievements;
    private readonly ICatalogService _catalog;
    private readonly ILogger<SampleDataSeeder> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="games">Game persistence.</param>
    /// <param name="collections">Collection persistence.</param>
    /// <param name="achievements">Achievement persistence.</param>
    /// <param name="catalog">Assigns catalog identity to the sample games.</param>
    /// <param name="logger">Logger for seeding diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SampleDataSeeder(
        IGameRepository games,
        ICollectionRepository collections,
        IAchievementRepository achievements,
        ICatalogService catalog,
        ILogger<SampleDataSeeder> logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> SeedAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _games.CountAsync(cancellationToken).ConfigureAwait(false);
        if (existing > 0)
        {
            _logger.LogInformation(
                "Library already contains {Count} games; sample data was not seeded.", existing);
            return false;
        }

        var now = DateTimeOffset.Now;

        var favourites = await AddCollectionAsync("Favourites", 0, now, cancellationToken).ConfigureAwait(false);
        var backlog = await AddCollectionAsync("Backlog", 1, now, cancellationToken).ConfigureAwait(false);
        var finished = await AddCollectionAsync("Finished", 2, now, cancellationToken).ConfigureAwait(false);

        // Playtimes are chosen to straddle the meta-achievement thresholds, so the
        // achievement pages have both earned and unearned entries to render.
        var seeds = new (string Title, long Playtime, int DaysSincePlayed, long Size, string[] Tags, int? Collection)[]
        {
            ("Aurora Drift",        146_400, 1,   7_412_000_000,  ["Racing", "Multiplayer"],       favourites),
            ("Hollow Signal",       38_700,  3,   22_800_000_000, ["Horror", "Single-player"],     favourites),
            ("Verdant Sky",         3_900,   12,  1_240_000_000,  ["Puzzle", "Relaxing"],          backlog),
            ("Ironhold Tactics",    212_000, 5,   9_600_000_000,  ["Strategy", "Turn-based"],      finished),
            ("Neon Rally",          720,     40,  3_150_000_000,  ["Racing", "Arcade"],            backlog),
            ("The Long Quiet",      0,       0,   540_000_000,    ["Narrative"],                   backlog),
            ("Starforge Online",    401_500, 2,   48_200_000_000, ["MMO", "Multiplayer"],          favourites),
            ("Cobalt Depths",       26_100,  18,  6_050_000_000,  ["Roguelike", "Single-player"],  null)
        };

        foreach (var seed in seeds)
        {
            // The sample games have no real executable, so the fingerprint falls
            // back to the title alone. That is enough for them to behave like any
            // other catalogued game.
            var catalogEntry = await _catalog
                .EnsureEntryAsync(seed.Title, executable: null, cancellationToken)
                .ConfigureAwait(false);

            var game = new Game
            {
                CatalogId = catalogEntry.CatalogId,
                Title = seed.Title,
                // Sample rows intentionally point at a path that does not exist.
                // The library surfaces that as "executable missing" rather than
                // pretending these are launchable.
                ExecutablePath = Path.Combine("C:\\Games", seed.Title.Replace(" ", string.Empty), "game.exe"),
                InstallDir = Path.Combine("C:\\Games", seed.Title.Replace(" ", string.Empty)),
                InstallSizeBytes = seed.Size,
                PlaytimeSeconds = seed.Playtime,
                LastPlayedAt = seed.Playtime > 0 ? now.AddDays(-seed.DaysSincePlayed) : null,
                DateAdded = now.AddDays(-60 + seeds.Length),
                Tags = seed.Tags,
                CollectionId = seed.Collection,
                Notes = null,
                SourceUrl = null
            };

            await _games.AddAsync(game, cancellationToken).ConfigureAwait(false);
            await AddSampleAchievementsAsync(catalogEntry.CatalogId, seed.Title, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Seeded {Games} sample games across {Collections} collections.", seeds.Length, 3);
        return true;
    }

    /// <summary>Creates one sample collection and returns its identifier.</summary>
    /// <param name="name">Collection name.</param>
    /// <param name="sortOrder">Sidebar position.</param>
    /// <param name="now">Creation timestamp.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    private async Task<int> AddCollectionAsync(
        string name,
        int sortOrder,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var collection = new Collection { Name = name, SortOrder = sortOrder, DateAdded = now };
        return await _collections.AddAsync(collection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds one achievement of each kind to a sample game, so the achievement UI
    /// and editor have all three configuration shapes to render.
    /// </summary>
    /// <param name="catalogId">Shared catalog identity of the owning title.</param>
    /// <param name="title">Game title, used in achievement descriptions.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    private async Task AddSampleAchievementsAsync(
        string catalogId,
        string title,
        CancellationToken cancellationToken)
    {
        var saveFileConfig = JsonSerializer.Serialize(new SaveFileTriggerConfig
        {
            SaveFilePath = Path.Combine("C:\\Games", title.Replace(" ", string.Empty), "save", "profile.json"),
            Format = SaveFileFormat.Json,
            FieldPath = "progress.chaptersCompleted",
            Comparison = ComparisonOperator.GreaterThanOrEqual,
            Value = "10"
        });

        var memoryConfig = JsonSerializer.Serialize(new MemoryTriggerConfig
        {
            ModuleName = "game.exe",
            Offset = "0x0012F3A0",
            ValueType = MemoryValueType.Int32,
            Comparison = ComparisonOperator.GreaterThanOrEqual,
            Value = "100"
        });

        var definitions = new[]
        {
            new AchievementDefinition
            {
                CatalogId = catalogId,
                ApiName = "ACH_FIRST_LAUNCH",
                Title = "Getting started",
                Description = $"Launch {title} for the first time.",
                Kind = AchievementKind.Meta,
                ProviderKey = MetaAchievementProvider.ProviderKey,
                TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig
                {
                    Metric = MetaMetric.FirstLaunch
                }),
                SortOrder = 0
            },
            new AchievementDefinition
            {
                CatalogId = catalogId,
                ApiName = "ACH_TEN_HOURS",
                Title = "Ten hours in",
                Description = $"Play {title} for ten hours.",
                Kind = AchievementKind.Meta,
                ProviderKey = MetaAchievementProvider.ProviderKey,
                TriggerConfigJson = JsonSerializer.Serialize(new MetaTriggerConfig
                {
                    Metric = MetaMetric.GameHours,
                    Threshold = 10
                }),
                ProgressTarget = 10,
                SortOrder = 1
            },
            new AchievementDefinition
            {
                CatalogId = catalogId,

                // Hidden until earned: the description is withheld by the UI, but
                // it synchronises exactly like any other achievement.
                IsHidden = true,
                ApiName = "ACH_CHAPTER_TEN",
                Title = "Chapter Ten",
                Description = $"Complete ten chapters of {title}.",
                Kind = AchievementKind.SaveFile,
                ProviderKey = SaveFileAchievementProvider.ProviderKey,
                TriggerConfigJson = saveFileConfig,

                // Progressive: renders as "n / 10" rather than merely locked.
                ProgressTarget = 10,
                SortOrder = 2
            },
            new AchievementDefinition
            {
                CatalogId = catalogId,
                ApiName = "ACH_CENTURY",
                Title = "Century",
                Description = "Reach a score of 100 in a single run.",
                Kind = AchievementKind.Memory,
                ProviderKey = MemoryAchievementProvider.ProviderKey,
                TriggerConfigJson = memoryConfig,
                ProgressTarget = 100,
                SortOrder = 3
            }
        };

        foreach (var definition in definitions)
        {
            await _achievements.AddDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
        }
    }
}
