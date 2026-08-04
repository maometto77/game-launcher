using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Achievements.Providers;

/// <summary>
/// Evaluates achievements by reading values from a running game's memory.
/// </summary>
/// <remarks>
/// Read-only inspection. See <see cref="IProcessMemoryReader"/> for the access
/// rights actually requested; nothing in this path can modify a running game.
/// </remarks>
public sealed class MemoryAchievementProvider : IAchievementProvider
{
    /// <summary>The key definitions use to select this provider.</summary>
    public const string ProviderKey = "memory";

    private readonly IProcessMemoryReader _reader;
    private readonly ILogger<MemoryAchievementProvider> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="reader">Reads values from the running process.</param>
    /// <param name="logger">Logger for evaluation diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MemoryAchievementProvider(IProcessMemoryReader reader, ILogger<MemoryAchievementProvider> logger)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => ProviderKey;

    /// <inheritdoc />
    public string DisplayName => "Memory";

    /// <inheritdoc />
    /// <remarks>
    /// Only while a game is running. Everything this reads lives in a process that
    /// no longer exists once the game exits, so the exit trigger is deliberately
    /// excluded — by then there is nothing to read.
    /// </remarks>
    public bool HandlesTrigger(AchievementTrigger trigger) =>
        trigger is AchievementTrigger.RunningPoll or AchievementTrigger.GameStarted or AchievementTrigger.Manual;

    /// <inheritdoc />
    public Task<IReadOnlyList<AchievementEvaluation>> EvaluateAsync(
        IReadOnlyList<AchievementDefinition> definitions,
        AchievementEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(context);

        if (!context.HasLiveProcess)
        {
            // Reported per definition rather than returning nothing, so the
            // editor's test button explains why instead of appearing to hang.
            return Task.FromResult<IReadOnlyList<AchievementEvaluation>>(
                definitions
                    .Select(definition => AchievementEvaluation.Unavailable(
                        definition.Id, "The game is not running, so its memory cannot be read."))
                    .ToArray());
        }

        var processId = context.ProcessId!.Value;
        var results = new List<AchievementEvaluation>(definitions.Count);

        // Reads are cached per address within the batch: several achievements
        // watching the same counter cost one read.
        var cache = new Dictionary<(string Module, long Offset, MemoryValueType Type), MemoryReadResult>();

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = MemoryTriggerConfig.TryParse(definition.TriggerConfigJson);

            if (config is null)
            {
                results.Add(AchievementEvaluation.Unavailable(
                    definition.Id, "The memory trigger configuration could not be read."));
                continue;
            }

            if (!config.Validate(out var configError) || !config.TryGetOffset(out var offset))
            {
                results.Add(AchievementEvaluation.Unavailable(
                    definition.Id, configError ?? $"'{config.Offset}' is not a valid offset."));
                continue;
            }

            var key = (config.ModuleName, offset, config.ValueType);

            if (!cache.TryGetValue(key, out var read))
            {
                read = _reader.ReadValue(processId, config.ModuleName, offset, config.ValueType);
                cache[key] = read;
            }

            if (!read.Found)
            {
                results.Add(AchievementEvaluation.Unavailable(definition.Id, read.Error!));
                continue;
            }

            var satisfied = AchievementComparison.Satisfies(read.Value, config.Comparison, config.Value);
            var progress = AchievementComparison.AsProgress(read.Value);

            results.Add(satisfied
                ? AchievementEvaluation.Unlock(definition.Id, progress)
                : AchievementEvaluation.NotYet(definition.Id, progress));
        }

        _logger.LogDebug(
            "Memory provider evaluated {Count} rules across {Reads} distinct addresses.",
            definitions.Count, cache.Count);

        return Task.FromResult<IReadOnlyList<AchievementEvaluation>>(results);
    }
}

/// <summary>
/// Holds achievements that are never evaluated automatically.
/// </summary>
/// <remarks>
/// <para>
/// Exists so that a definition can be authored, displayed and synchronised
/// without any rule attached — an achievement awarded by an external tool, an
/// imported set whose provider is not installed, or one a future in-game API will
/// unlock.
/// </para>
/// <para>
/// It also demonstrates the extension point: this provider is thirty lines and
/// required no change to the engine, the schema or the interface.
/// </para>
/// </remarks>
public sealed class ManualAchievementProvider : IAchievementProvider
{
    /// <summary>The key definitions use to select this provider.</summary>
    public const string ProviderKey = "manual";

    /// <inheritdoc />
    public string Key => ProviderKey;

    /// <inheritdoc />
    public string DisplayName => "Manual";

    /// <inheritdoc />
    /// <remarks>
    /// Handles nothing. Manual achievements are unlocked by something outside the
    /// evaluation loop, so running them costs work and can never change anything.
    /// </remarks>
    public bool HandlesTrigger(AchievementTrigger trigger) => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<AchievementEvaluation>> EvaluateAsync(
        IReadOnlyList<AchievementDefinition> definitions,
        AchievementEvaluationContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AchievementEvaluation>>([]);
}
