using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// Describes an achievement that has just been earned.
/// </summary>
/// <param name="Definition">The achievement.</param>
/// <param name="Game">The game it belongs to, or <see langword="null"/> for a library-wide one.</param>
/// <param name="UnlockedAt">When it was earned.</param>
public sealed record AchievementUnlockedEventArgs(
    AchievementDefinition Definition,
    Game? Game,
    DateTimeOffset UnlockedAt);

/// <summary>
/// A registered provider, described for display and validation.
/// </summary>
/// <param name="Key">The provider's dispatch key.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <remarks>
/// Exposed rather than handing out the providers themselves, so the editor can
/// offer a choice and warn about a missing one without acquiring the ability to
/// invoke evaluation directly.
/// </remarks>
public sealed record AchievementProviderDescriptor(string Key, string DisplayName);

/// <summary>
/// The outcome of one evaluation pass.
/// </summary>
/// <param name="Evaluated">How many definitions were considered.</param>
/// <param name="Unlocked">How many were newly earned.</param>
/// <param name="ProgressUpdated">How many had their progress advanced.</param>
public sealed record AchievementEvaluationResult(int Evaluated, int Unlocked, int ProgressUpdated)
{
    /// <summary>A result for a pass with nothing to do.</summary>
    public static AchievementEvaluationResult Nothing { get; } = new(0, 0, 0);
}

/// <summary>
/// Runs achievement providers and records what they decide.
/// </summary>
/// <remarks>
/// <para>
/// The engine owns persistence and notification; providers own the decision.
/// That split is what keeps evaluation independent of synchronisation — nothing
/// here knows a relay exists, and the sync service never evaluates anything. An
/// unlock earned offline is simply a row with no <c>SyncedAt</c>.
/// </para>
/// <para>
/// Evaluation is idempotent. Unlocks are insert-only and the engine raises
/// <see cref="AchievementUnlocked"/> solely on the transition, so re-running a
/// pass over an already-earned achievement neither duplicates the row, moves its
/// timestamp, nor notifies a second time.
/// </para>
/// </remarks>
public interface IAchievementEngine
{
    /// <summary>
    /// Raised on the UI thread when an achievement is earned for the first time.
    /// </summary>
    /// <remarks>
    /// The engine's entire contribution to notification. What a toast looks like,
    /// whether one appears at all, and how long it lingers are decisions for the
    /// interface, not for the evaluator.
    /// </remarks>
    event EventHandler<AchievementUnlockedEventArgs>? AchievementUnlocked;

    /// <summary>
    /// Gets every registered provider, ordered by display name.
    /// </summary>
    /// <remarks>
    /// The editor populates its provider list from this, so a newly registered
    /// provider becomes authorable with no change to the interface.
    /// </remarks>
    IReadOnlyList<AchievementProviderDescriptor> Providers { get; }

    /// <summary>
    /// Determines whether a provider key is backed by a registered provider.
    /// </summary>
    /// <param name="providerKey">The key to check.</param>
    /// <returns>
    /// <see langword="true"/> when a provider with that key is installed.
    /// </returns>
    /// <remarks>
    /// Lets the interface state plainly that a definition will never be evaluated,
    /// rather than leaving it looking merely unearned. A definition naming an
    /// unknown provider stays intact — see
    /// <see cref="AchievementDefinition.ProviderKey"/>.
    /// </remarks>
    bool IsProviderAvailable(string? providerKey);

    /// <summary>
    /// Evaluates the achievements belonging to one game.
    /// </summary>
    /// <param name="game">The game to evaluate.</param>
    /// <param name="trigger">What caused the pass.</param>
    /// <param name="processId">Process identifier when the game is running.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass achieved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> is <see langword="null"/>.</exception>
    Task<AchievementEvaluationResult> EvaluateGameAsync(
        Game game,
        AchievementTrigger trigger,
        int? processId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates achievements that belong to no single game.
    /// </summary>
    /// <param name="trigger">What caused the pass.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass achieved.</returns>
    Task<AchievementEvaluationResult> EvaluateLibraryAsync(
        AchievementTrigger trigger,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates a single definition and reports the verdict without recording it.
    /// </summary>
    /// <param name="definition">The definition to test.</param>
    /// <param name="game">The game it belongs to, if any.</param>
    /// <param name="processId">Process identifier when the game is running.</param>
    /// <param name="cancellationToken">Cancels the test.</param>
    /// <returns>The provider's verdict, or <see langword="null"/> when no provider handles it.</returns>
    /// <remarks>
    /// Powers the editor's test button. Deliberately does not persist: somebody
    /// checking whether an offset is right should not thereby award themselves the
    /// achievement.
    /// </remarks>
    Task<AchievementEvaluation?> TestAsync(
        AchievementDefinition definition,
        Game? game,
        int? processId = null,
        CancellationToken cancellationToken = default);
}
