namespace GameLauncher.Desktop.Models;

/// <summary>
/// The mechanism used to decide whether an achievement has been earned.
/// </summary>
/// <remarks>
/// The kind determines how <see cref="AchievementDefinition.TriggerConfigJson"/>
/// is interpreted; each kind has its own configuration shape and its own
/// evaluator.
/// </remarks>
public enum AchievementKind
{
    /// <summary>
    /// Computed purely from data the launcher already owns, such as playtime
    /// totals or library completion. Always available and never requires the
    /// game to cooperate.
    /// </summary>
    Meta = 0,

    /// <summary>
    /// Evaluated by reading a value out of the game's save file using a
    /// declarative rule (JSON path, XPath, INI key or regex).
    /// </summary>
    SaveFile = 1,

    /// <summary>
    /// Evaluated by reading a value from the running game's memory. Strictly
    /// read-only inspection.
    /// </summary>
    Memory = 2
}
