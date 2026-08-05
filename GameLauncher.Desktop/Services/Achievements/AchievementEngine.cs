using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// Default <see cref="IAchievementEngine"/>.
/// </summary>
/// <remarks>
/// Dispatches by <see cref="AchievementDefinition.ProviderKey"/> against whatever
/// providers are registered. It has no knowledge of any particular provider, so
/// adding one is a container registration and nothing else.
/// </remarks>
public sealed class AchievementEngine : IAchievementEngine
{
    private readonly IReadOnlyDictionary<string, IAchievementProvider> _providers;
    private readonly IAchievementRepository _achievements;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<AchievementEngine> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="providers">Every registered provider.</param>
    /// <param name="achievements">Achievement persistence.</param>
    /// <param name="dispatcher">Marshals unlock events onto the UI thread.</param>
    /// <param name="logger">Logger for evaluation diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Two providers claim the same key.</exception>
    public AchievementEngine(
        IEnumerable<IAchievementProvider> providers,
        IAchievementRepository achievements,
        IUiDispatcher dispatcher,
        ILogger<AchievementEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var byKey = new Dictionary<string, IAchievementProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            // Failing loudly at construction: two providers sharing a key would
            // mean definitions were silently evaluated by whichever won, which is
            // far harder to diagnose than a startup error.
            if (!byKey.TryAdd(provider.Key, provider))
            {
                throw new InvalidOperationException(
                    $"Two achievement providers both claim the key '{provider.Key}'.");
            }
        }

        _providers = byKey;

        Providers = byKey.Values
            .Select(provider => new AchievementProviderDescriptor(provider.Key, provider.DisplayName))
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _logger.LogDebug("Achievement engine loaded {Count} providers: {Keys}.",
            byKey.Count, string.Join(", ", byKey.Keys));
    }

    /// <inheritdoc />
    public event EventHandler<AchievementUnlockedEventArgs>? AchievementUnlocked;

    /// <inheritdoc />
    public IReadOnlyList<AchievementProviderDescriptor> Providers { get; }

    /// <inheritdoc />
    public bool IsProviderAvailable(string? providerKey) =>
        !string.IsNullOrWhiteSpace(providerKey) && _providers.ContainsKey(providerKey);

    /// <inheritdoc />
    public async Task<AchievementEvaluationResult> EvaluateGameAsync(
        Game game,
        AchievementTrigger trigger,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (string.IsNullOrWhiteSpace(game.CatalogId))
        {
            // Achievements hang off catalog identity, so a game without one has
            // none. Not an error: it simply has not been catalogued yet.
            return AchievementEvaluationResult.Nothing;
        }

        var definitions = await _achievements
            .GetDefinitionsForCatalogAsync(game.CatalogId, cancellationToken)
            .ConfigureAwait(false);

        var context = new AchievementEvaluationContext(trigger, game, game.CatalogId, processId);

        return await RunAsync(definitions, context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AchievementEvaluationResult> EvaluateLibraryAsync(
        AchievementTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        var definitions = await _achievements
            .GetLibraryWideDefinitionsAsync(cancellationToken)
            .ConfigureAwait(false);

        var context = new AchievementEvaluationContext(trigger, Game: null, CatalogId: null, ProcessId: null);

        return await RunAsync(definitions, context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately does not route through <see cref="RunAsync"/>. Persistence and
    /// notification both live there, so testing a rule reaches neither the
    /// repository nor <see cref="AchievementUnlocked"/> — it asks the provider and
    /// returns the answer. Keeping the two paths separate is what makes the
    /// guarantee structural rather than a matter of remembering to skip a step.
    /// </remarks>
    public async Task<AchievementEvaluation?> TestAsync(
        AchievementDefinition definition,
        Game? game,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!_providers.TryGetValue(definition.ProviderKey, out var provider))
        {
            return null;
        }

        var context = new AchievementEvaluationContext(
            AchievementTrigger.Manual, game, game?.CatalogId, processId);

        try
        {
            var results = await provider
                .EvaluateAsync([definition], context, cancellationToken)
                .ConfigureAwait(false);

            return results.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Surfaced as a diagnostic rather than thrown: the editor's test
            // button should report what went wrong, not crash the dialog.
            return AchievementEvaluation.Unavailable(definition.Id, ex.Message);
        }
    }

    /// <summary>
    /// Runs the providers over a set of definitions and records the outcome.
    /// </summary>
    /// <param name="definitions">Definitions to consider.</param>
    /// <param name="context">What is being evaluated and why.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass achieved.</returns>
    private async Task<AchievementEvaluationResult> RunAsync(
        IReadOnlyList<AchievementDefinition> definitions,
        AchievementEvaluationContext context,
        CancellationToken cancellationToken)
    {
        if (definitions.Count == 0)
        {
            return AchievementEvaluationResult.Nothing;
        }

        var unlockedIds = await _achievements
            .GetUnlockedDefinitionIdsAsync(cancellationToken)
            .ConfigureAwait(false);

        // Already-earned achievements are never re-evaluated. That is the first
        // and cheapest guarantee of idempotence: a provider cannot move a
        // timestamp it is never asked about.
        var pending = definitions.Where(definition => !unlockedIds.Contains(definition.Id)).ToList();

        if (pending.Count == 0)
        {
            return AchievementEvaluationResult.Nothing;
        }

        var evaluated = 0;
        var unlocked = 0;
        var progressed = 0;

        // Grouped so each provider is invoked once with everything it owns, rather
        // than once per achievement — one save file read for ten rules against it.
        foreach (var group in pending.GroupBy(definition => definition.ProviderKey, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_providers.TryGetValue(group.Key, out var provider))
            {
                // A definition authored for a provider that is not installed is
                // left alone rather than guessed at.
                _logger.LogDebug("No provider is registered for key {Key}; skipping.", group.Key);
                continue;
            }

            if (!provider.HandlesTrigger(context.Trigger))
            {
                continue;
            }

            var batch = group.ToList();
            evaluated += batch.Count;

            IReadOnlyList<AchievementEvaluation> results;
            try
            {
                results = await provider.EvaluateAsync(batch, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One misbehaving provider must not stop the others. A memory
                // reader failing against a protected process is routine.
                _logger.LogError(ex, "Achievement provider {Key} threw during evaluation.", provider.Key);
                continue;
            }

            var (justUnlocked, justProgressed) = await ApplyAsync(
                results, batch, context, cancellationToken).ConfigureAwait(false);

            unlocked += justUnlocked;
            progressed += justProgressed;
        }

        return new AchievementEvaluationResult(evaluated, unlocked, progressed);
    }

    /// <summary>
    /// Persists verdicts and raises events for genuine unlocks.
    /// </summary>
    /// <param name="results">The provider's verdicts.</param>
    /// <param name="batch">The definitions they refer to.</param>
    /// <param name="context">The evaluation context, for the event payload.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>How many were unlocked and how many advanced.</returns>
    private async Task<(int Unlocked, int Progressed)> ApplyAsync(
        IReadOnlyList<AchievementEvaluation> results,
        IReadOnlyList<AchievementDefinition> batch,
        AchievementEvaluationContext context,
        CancellationToken cancellationToken)
    {
        var byId = batch.ToDictionary(definition => definition.Id);
        var unlocked = 0;
        var progressed = 0;

        foreach (var result in results)
        {
            if (!byId.TryGetValue(result.DefinitionId, out var definition))
            {
                _logger.LogWarning(
                    "A provider returned a verdict for definition {Id}, which it was not asked about.",
                    result.DefinitionId);
                continue;
            }

            if (result.Diagnostic is { } diagnostic)
            {
                _logger.LogDebug(
                    "Achievement {ApiName} could not be evaluated: {Reason}", definition.ApiName, diagnostic);
            }

            if (result.Progress is { } progress &&
                await _achievements
                    .RecordProgressAsync(definition.Id, progress, DateTimeOffset.Now, cancellationToken)
                    .ConfigureAwait(false))
            {
                progressed++;
            }

            if (!result.ShouldUnlock)
            {
                continue;
            }

            var unlockedAt = DateTimeOffset.Now;

            // Returns true only on the transition. Everything downstream — the
            // event, the toast, the count — hangs off that, which is what makes
            // repeated evaluation silent rather than noisy.
            if (!await _achievements
                    .UnlockAsync(definition.Id, unlockedAt, cancellationToken)
                    .ConfigureAwait(false))
            {
                continue;
            }

            unlocked++;

            _logger.LogInformation(
                "Achievement unlocked: {Title} ({ApiName}).", definition.Title, definition.ApiName);

            var payload = new AchievementUnlockedEventArgs(definition, context.Game, unlockedAt);
            _dispatcher.Invoke(() => AchievementUnlocked?.Invoke(this, payload));
        }

        return (unlocked, progressed);
    }
}
