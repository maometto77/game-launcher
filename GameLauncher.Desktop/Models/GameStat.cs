namespace GameLauncher.Desktop.Models;

/// <summary>
/// The value domain of a stat.
/// </summary>
public enum GameStatType
{
    /// <summary>A whole-number counter, such as matches played.</summary>
    Integer = 0,

    /// <summary>A fractional measure, such as hours survived or distance travelled.</summary>
    Float = 1
}

/// <summary>
/// The definition of a named counter a game accumulates.
/// </summary>
/// <remarks>
/// <para>
/// Stats are the mechanism behind progressive achievements: an achievement names
/// a stat through <see cref="AchievementDefinition.StatApiName"/> and a target,
/// and the achievement engine compares the two. Modelling stats separately means
/// several achievements can share one counter ("play 10 / 100 / 1000 matches")
/// without evaluating the same source three times.
/// </para>
/// <para>
/// Definitions are separate from <see cref="GameStatValue"/> so a shared or
/// exported catalog can carry the definitions without carrying anybody's
/// personal numbers.
/// </para>
/// </remarks>
public sealed class GameStatDefinition
{
    /// <summary>Auto-incrementing primary key. Local to this database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The catalog entry this stat belongs to, or <see langword="null"/> for a
    /// library-wide stat such as total games launched.
    /// </summary>
    /// <remarks>
    /// Keyed on shared catalog identity for the same reason achievements are: a
    /// stat definition is a property of the title, so one definition serves every
    /// user who owns it.
    /// </remarks>
    public string? CatalogId { get; set; }

    /// <summary>
    /// Stable, human-readable handle within the game, such as <c>STAT_MATCHES</c>.
    /// </summary>
    public string ApiName { get; set; } = string.Empty;

    /// <summary>Name shown in the user interface.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The value domain of this stat.</summary>
    public GameStatType StatType { get; set; }

    /// <summary>Value assumed before anything has been recorded.</summary>
    public double DefaultValue { get; set; }

    /// <summary>
    /// Whether the stat may only ever increase.
    /// </summary>
    /// <remarks>
    /// True for lifetime totals, which must not be reduced by a save file being
    /// rolled back or a memory read landing on a per-run counter that has just
    /// reset. False for genuine gauges such as a current level.
    /// </remarks>
    public bool IsIncrementOnly { get; set; } = true;

    /// <summary>Stable machine-independent identity, as 32 lowercase hexadecimal characters.</summary>
    public string GlobalKey { get; set; } = string.Empty;

    /// <summary>When this definition was last modified locally.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The current value of a stat for this installation.
/// </summary>
public sealed class GameStatValue
{
    /// <summary>Identifier of the <see cref="GameStatDefinition"/> this value belongs to.</summary>
    public int StatId { get; set; }

    /// <summary>The current value.</summary>
    public double Value { get; set; }

    /// <summary>When the value was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// When this value was last pushed to a relay, or <see langword="null"/> if
    /// never.
    /// </summary>
    public DateTimeOffset? SyncedAt { get; set; }
}
