using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// What caused an evaluation pass to run.
/// </summary>
/// <remarks>
/// Providers use this to skip work that cannot possibly have changed. A save-file
/// rule has nothing to do on a memory poll, and re-reading every save file two
/// times a second would be the difference between a launcher and a disk grinder.
/// </remarks>
public enum AchievementTrigger
{
    /// <summary>The launcher started.</summary>
    Startup = 0,

    /// <summary>A game was launched.</summary>
    GameStarted = 1,

    /// <summary>A game exited.</summary>
    GameExited = 2,

    /// <summary>A watched save file changed.</summary>
    SaveFileChanged = 3,

    /// <summary>The periodic poll while a game is running.</summary>
    RunningPoll = 4,

    /// <summary>The user asked for a re-check.</summary>
    Manual = 5
}

/// <summary>
/// Everything a provider is given to decide with.
/// </summary>
/// <param name="Trigger">What caused this pass.</param>
/// <param name="Game">The game concerned, or <see langword="null"/> for a library-wide pass.</param>
/// <param name="CatalogId">Shared catalog identity of that game, if it has one.</param>
/// <param name="ProcessId">
/// Process identifier of the running game, or <see langword="null"/> when it is
/// not running.
/// </param>
public sealed record AchievementEvaluationContext(
    AchievementTrigger Trigger,
    Game? Game,
    string? CatalogId,
    int? ProcessId)
{
    /// <summary>Gets a value indicating whether a live process is available to inspect.</summary>
    public bool HasLiveProcess => ProcessId is > 0;
}

/// <summary>
/// A provider's verdict on one achievement.
/// </summary>
/// <param name="DefinitionId">The definition this concerns.</param>
/// <param name="ShouldUnlock">Whether the achievement's condition is met.</param>
/// <param name="Progress">
/// How far towards the target the observed value is, or <see langword="null"/>
/// when the provider cannot express partial progress.
/// </param>
/// <param name="Diagnostic">
/// Why the provider could not evaluate, for the editor's test button and the log.
/// Never shown as a failure to the user during normal play.
/// </param>
public sealed record AchievementEvaluation(
    int DefinitionId,
    bool ShouldUnlock,
    double? Progress = null,
    string? Diagnostic = null)
{
    /// <summary>Creates a verdict that the achievement has been earned.</summary>
    /// <param name="definitionId">The definition concerned.</param>
    /// <param name="progress">Observed value, if meaningful.</param>
    /// <returns>An unlocking verdict.</returns>
    public static AchievementEvaluation Unlock(int definitionId, double? progress = null) =>
        new(definitionId, ShouldUnlock: true, progress);

    /// <summary>Creates a verdict that the achievement has not been earned.</summary>
    /// <param name="definitionId">The definition concerned.</param>
    /// <param name="progress">Observed value, if meaningful.</param>
    /// <returns>A non-unlocking verdict.</returns>
    public static AchievementEvaluation NotYet(int definitionId, double? progress = null) =>
        new(definitionId, ShouldUnlock: false, progress);

    /// <summary>Creates a verdict that the achievement could not be evaluated.</summary>
    /// <param name="definitionId">The definition concerned.</param>
    /// <param name="reason">Why evaluation was not possible.</param>
    /// <returns>A verdict carrying a diagnostic.</returns>
    /// <remarks>
    /// Distinct from "not yet": a save file that is missing is a configuration
    /// problem worth reporting in the editor, whereas a counter sitting at 40 out
    /// of 100 is the system working correctly.
    /// </remarks>
    public static AchievementEvaluation Unavailable(int definitionId, string reason) =>
        new(definitionId, ShouldUnlock: false, null, reason);
}

/// <summary>
/// Decides whether achievements of one kind have been earned.
/// </summary>
/// <remarks>
/// <para>
/// A provider's only job is to answer the question. It does not persist unlocks,
/// raise notifications, or know that a relay exists — those belong to the engine
/// and the sync service respectively. Keeping the decision pure is what makes a
/// provider testable with no database and no network.
/// </para>
/// <para>
/// Adding a provider is a registration: implement this interface with a new
/// <see cref="Key"/> and add it to the container. The engine dispatches by key
/// and needs no change.
/// </para>
/// </remarks>
public interface IAchievementProvider
{
    /// <summary>
    /// Stable key matching <c>AchievementDefinition.ProviderKey</c>.
    /// </summary>
    /// <remarks>
    /// A string rather than an enum member precisely so that a new provider does
    /// not require editing the core model.
    /// </remarks>
    string Key { get; }

    /// <summary>Human-readable name, shown in the achievement list and the editor.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Determines whether this provider has anything to do for a trigger.
    /// </summary>
    /// <param name="trigger">What caused the pass.</param>
    /// <returns><see langword="true"/> when the provider should be invoked.</returns>
    bool HandlesTrigger(AchievementTrigger trigger);

    /// <summary>
    /// Evaluates a batch of definitions.
    /// </summary>
    /// <param name="definitions">Definitions belonging to this provider. Never empty.</param>
    /// <param name="context">What is being evaluated and why.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>One verdict per definition the provider could reach a view on.</returns>
    /// <remarks>
    /// Batched so that a provider can read a save file once for the ten rules
    /// that reference it, or open a process handle once for every memory rule,
    /// rather than repeating that work per achievement.
    /// </remarks>
    Task<IReadOnlyList<AchievementEvaluation>> EvaluateAsync(
        IReadOnlyList<AchievementDefinition> definitions,
        AchievementEvaluationContext context,
        CancellationToken cancellationToken = default);
}
