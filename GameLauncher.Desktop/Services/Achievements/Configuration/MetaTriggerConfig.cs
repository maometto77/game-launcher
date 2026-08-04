using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Desktop.Services.Achievements.Configuration;

/// <summary>
/// The quantity a meta achievement measures.
/// </summary>
public enum MetaMetric
{
    /// <summary>Whether the game has ever been launched. Wire token <c>firstLaunch</c>.</summary>
    FirstLaunch = 0,

    /// <summary>Hours played in this game. Wire token <c>gameHours</c>.</summary>
    GameHours = 1,

    /// <summary>Hours played across the whole library. Wire token <c>libraryHours</c>.</summary>
    LibraryHours = 2,

    /// <summary>Number of games in the library. Wire token <c>gamesOwned</c>.</summary>
    GamesOwned = 3,

    /// <summary>Number of completed sessions for this game. Wire token <c>sessions</c>.</summary>
    Sessions = 4,

    /// <summary>
    /// Fraction of a collection that has been played, as a percentage.
    /// Wire token <c>collectionCompletion</c>.
    /// </summary>
    CollectionCompletion = 5
}

/// <summary>Converts <see cref="MetaMetric"/> to and from its wire tokens.</summary>
public sealed class MetaMetricConverter : TokenEnumConverter<MetaMetric>
{
    /// <summary>Initialises a new instance.</summary>
    public MetaMetricConverter()
        : base(new Dictionary<string, MetaMetric>
        {
            ["firstLaunch"] = MetaMetric.FirstLaunch,
            ["gameHours"] = MetaMetric.GameHours,
            ["libraryHours"] = MetaMetric.LibraryHours,
            ["gamesOwned"] = MetaMetric.GamesOwned,
            ["sessions"] = MetaMetric.Sessions,
            ["collectionCompletion"] = MetaMetric.CollectionCompletion
        })
    {
    }
}

/// <summary>
/// Trigger configuration for a meta achievement.
/// </summary>
/// <remarks>
/// Meta achievements are computed entirely from data the launcher already holds,
/// so they need no cooperation from the game and work for every title in the
/// library from the moment it is added.
/// </remarks>
public sealed record MetaTriggerConfig
{
    /// <summary>What is being measured.</summary>
    [JsonPropertyName("metric")]
    [JsonConverter(typeof(MetaMetricConverter))]
    public MetaMetric Metric { get; init; } = MetaMetric.FirstLaunch;

    /// <summary>
    /// The value the metric must reach.
    /// </summary>
    /// <remarks>
    /// Ignored for <see cref="MetaMetric.FirstLaunch"/>, which is inherently a
    /// yes-or-no question.
    /// </remarks>
    [JsonPropertyName("threshold")]
    public double Threshold { get; init; } = 1;

    /// <summary>
    /// The collection being measured, for
    /// <see cref="MetaMetric.CollectionCompletion"/>.
    /// </summary>
    [JsonPropertyName("collectionId")]
    public int? CollectionId { get; init; }

    /// <summary>
    /// Parses a configuration from stored JSON.
    /// </summary>
    /// <param name="json">The stored configuration text.</param>
    /// <returns>The parsed configuration, or <see langword="null"/> when malformed.</returns>
    public static MetaTriggerConfig? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MetaTriggerConfig>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
