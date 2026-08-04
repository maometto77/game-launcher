using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Achievements.Providers;

/// <summary>
/// Evaluates achievements computed from data the launcher already holds.
/// </summary>
/// <remarks>
/// Needs no cooperation from the game and no configuration beyond a metric and a
/// threshold, so these work for every title from the moment it is added. Playtime
/// is read from the game's running total, which is derived from play sessions —
/// the same figures the library shows.
/// </remarks>
public sealed class MetaAchievementProvider : IAchievementProvider
{
    /// <summary>The key definitions use to select this provider.</summary>
    public const string ProviderKey = "meta";

    private readonly IGameRepository _games;
    private readonly IPlaySessionRepository _sessions;
    private readonly ILogger<MetaAchievementProvider> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="games">Supplies playtime and library totals.</param>
    /// <param name="sessions">Supplies session counts.</param>
    /// <param name="logger">Logger for evaluation diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MetaAchievementProvider(
        IGameRepository games,
        IPlaySessionRepository sessions,
        ILogger<MetaAchievementProvider> logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => ProviderKey;

    /// <inheritdoc />
    public string DisplayName => "Meta";

    /// <inheritdoc />
    /// <remarks>
    /// Skipped on the running poll: none of these metrics can change while a game
    /// is mid-session, because playtime is only credited when the process exits.
    /// Polling them twice a second would be pure waste.
    /// </remarks>
    public bool HandlesTrigger(AchievementTrigger trigger) =>
        trigger is not AchievementTrigger.RunningPoll and not AchievementTrigger.SaveFileChanged;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AchievementEvaluation>> EvaluateAsync(
        IReadOnlyList<AchievementDefinition> definitions,
        AchievementEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(context);

        // Gathered once for the whole batch rather than per achievement: a library
        // with ten playtime milestones should cost one query, not ten.
        var facts = await GatherAsync(context, cancellationToken).ConfigureAwait(false);

        var results = new List<AchievementEvaluation>(definitions.Count);

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = MetaTriggerConfig.TryParse(definition.TriggerConfigJson);

            if (config is null)
            {
                results.Add(AchievementEvaluation.Unavailable(
                    definition.Id, "The meta trigger configuration could not be read."));
                continue;
            }

            results.Add(Evaluate(definition, config, facts));
        }

        return results;
    }

    /// <summary>Applies one rule to the gathered facts.</summary>
    /// <param name="definition">The achievement being evaluated.</param>
    /// <param name="config">Its rule.</param>
    /// <param name="facts">Values read once for the batch.</param>
    /// <returns>The verdict.</returns>
    private static AchievementEvaluation Evaluate(
        AchievementDefinition definition,
        MetaTriggerConfig config,
        MetaFacts facts)
    {
        if (config.Metric == MetaMetric.FirstLaunch)
        {
            return facts.HasEverBeenPlayed
                ? AchievementEvaluation.Unlock(definition.Id, 1)
                : AchievementEvaluation.NotYet(definition.Id, 0);
        }

        var observed = config.Metric switch
        {
            MetaMetric.GameHours => facts.GameHours,
            MetaMetric.LibraryHours => facts.LibraryHours,
            MetaMetric.GamesOwned => facts.GamesOwned,
            MetaMetric.Sessions => facts.SessionCount,
            MetaMetric.CollectionCompletion => facts.CollectionCompletionPercent,
            _ => 0d
        };

        // Progress is reported whether or not the threshold is met, so the UI can
        // show "6.2 of 10 hours" rather than merely "locked".
        return observed >= config.Threshold
            ? AchievementEvaluation.Unlock(definition.Id, observed)
            : AchievementEvaluation.NotYet(definition.Id, observed);
    }

    /// <summary>Reads every value the batch might need, once.</summary>
    /// <param name="context">What is being evaluated.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The gathered facts.</returns>
    private async Task<MetaFacts> GatherAsync(
        AchievementEvaluationContext context,
        CancellationToken cancellationToken)
    {
        var game = context.Game;

        var libraryHours = await _games
            .GetTotalPlaytimeSecondsAsync(cancellationToken)
            .ConfigureAwait(false) / 3600d;

        var gamesOwned = await _games.CountAsync(cancellationToken).ConfigureAwait(false);

        var gameHours = game is null ? 0d : game.PlaytimeSeconds / 3600d;

        var sessionCount = game is null
            ? 0
            : await _sessions.CountForGameAsync(game.Id, cancellationToken).ConfigureAwait(false);

        var completion = await CalculateCollectionCompletionAsync(game, cancellationToken).ConfigureAwait(false);

        return new MetaFacts(
            // "Ever played" is derived from LastPlayedAt rather than from playtime
            // being above zero: a session shorter than a second still happened.
            HasEverBeenPlayed: game?.LastPlayedAt is not null,
            GameHours: gameHours,
            LibraryHours: libraryHours,
            GamesOwned: gamesOwned,
            SessionCount: sessionCount,
            CollectionCompletionPercent: completion);
    }

    /// <summary>
    /// Works out how much of a game's collection has been played.
    /// </summary>
    /// <param name="game">The game whose collection is measured.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A percentage between zero and one hundred.</returns>
    private async Task<double> CalculateCollectionCompletionAsync(Game? game, CancellationToken cancellationToken)
    {
        if (game?.CollectionId is not { } collectionId)
        {
            return 0d;
        }

        try
        {
            var members = await _games.GetByCollectionAsync(collectionId, cancellationToken).ConfigureAwait(false);

            if (members.Count == 0)
            {
                return 0d;
            }

            var played = members.Count(member => member.LastPlayedAt is not null);
            return played * 100d / members.Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not measure completion for collection {CollectionId}.", collectionId);
            return 0d;
        }
    }

    /// <summary>Values read once and shared across a batch.</summary>
    /// <param name="HasEverBeenPlayed">Whether the game has ever been launched.</param>
    /// <param name="GameHours">Hours played in this game.</param>
    /// <param name="LibraryHours">Hours played across the library.</param>
    /// <param name="GamesOwned">Number of games in the library.</param>
    /// <param name="SessionCount">Completed sessions for this game.</param>
    /// <param name="CollectionCompletionPercent">Percentage of the game's collection that has been played.</param>
    private sealed record MetaFacts(
        bool HasEverBeenPlayed,
        double GameHours,
        double LibraryHours,
        int GamesOwned,
        int SessionCount,
        double CollectionCompletionPercent);
}
