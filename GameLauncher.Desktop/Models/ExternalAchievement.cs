namespace GameLauncher.Desktop.Models;

/// <summary>
/// What a row read from a local achievement file describes.
/// </summary>
public enum ExternalAchievementKind
{
    /// <summary>
    /// An achievement: something with a locked and an unlocked state.
    /// </summary>
    Achievement = 0,

    /// <summary>
    /// A statistic: a counter with no unlocked state of its own.
    /// </summary>
    /// <remarks>
    /// Kept rather than discarded because a stat is usually what an achievement
    /// counts towards, and a page that can say "4,213 of 5,000" is far more use
    /// than one that can only say "locked".
    /// </remarks>
    Statistic = 1
}

/// <summary>
/// One achievement or statistic read from a local achievement file.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="AchievementDefinition"/>. A definition
/// is something this launcher's catalogue describes and its providers decide
/// about; this is an observation of what some other program wrote to disk. They
/// meet at the game, not in the same table — conflating them would let a file on
/// disk write rows into a catalogue that is meant to be curated.
/// </para>
/// <para>
/// Identity is source, application and API name together. The same game unlocked
/// under two different emulators is two rows, which is correct: they are two
/// separate records of play and neither is authoritative over the other.
/// </para>
/// </remarks>
public sealed class ExternalAchievement
{
    /// <summary>Auto-incrementing primary key.</summary>
    public long Id { get; set; }

    /// <summary>Which reader produced this row.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Steam application id the file belongs to.</summary>
    public int SteamAppId { get; set; }

    /// <summary>The achievement's API name, as the game defines it.</summary>
    /// <remarks>
    /// Not a display name. These are identifiers like <c>ACH_WIN_ONE_GAME</c>,
    /// which is what a game's own achievement metadata is keyed by, and what a
    /// nicer title would have to be looked up from.
    /// </remarks>
    public string ApiName { get; set; } = string.Empty;

    /// <summary>What this row describes.</summary>
    public ExternalAchievementKind Kind { get; set; }

    /// <summary>Whether it has been earned.</summary>
    public bool IsUnlocked { get; set; }

    /// <summary>When it was earned, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Null for a locked achievement, and also for an unlocked one whose file
    /// recorded no time — some writers store the flag and not the timestamp.
    /// Distinguished from a zero time, which several of them use to mean "never".
    /// </remarks>
    public DateTimeOffset? UnlockedAt { get; set; }

    /// <summary>Progress so far, when the file reports any.</summary>
    public double? CurrentValue { get; set; }

    /// <summary>What progress is measured against, when the file says.</summary>
    public double? TargetValue { get; set; }

    /// <summary>File this row was read from.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>When the launcher last saw it.</summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>
    /// Gets progress as a fraction, or <see langword="null"/> when it cannot be
    /// expressed as one.
    /// </summary>
    public double? Fraction =>
        TargetValue is > 0 && CurrentValue is { } current
            ? Math.Clamp(current / TargetValue.Value, 0d, 1d)
            : IsUnlocked ? 1d : null;

    /// <summary>Gets the identity this row is matched on.</summary>
    public string Identity =>
        $"{SourceKey}:{SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{ApiName}";
}

/// <summary>
/// Everything one achievement file said.
/// </summary>
/// <param name="SteamAppId">The application the file belongs to.</param>
/// <param name="Entries">Achievements and statistics, in file order.</param>
public sealed record ExternalAchievementSnapshot(int SteamAppId, IReadOnlyList<ExternalAchievement> Entries)
{
    /// <summary>An empty snapshot, for a file that said nothing usable.</summary>
    public static ExternalAchievementSnapshot Empty { get; } = new(0, []);

    /// <summary>Gets how many entries are unlocked.</summary>
    public int UnlockedCount => Entries.Count(entry => entry.IsUnlocked);
}
