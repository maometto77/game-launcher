using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Achievements.Providers;

/// <summary>
/// Evaluates achievements by reading a value out of a game's save file.
/// </summary>
/// <remarks>
/// Generic by construction: a rule names a file, a format, a path within it, a
/// comparison and a target, so supporting a new game means writing a rule rather
/// than writing code.
/// </remarks>
public sealed class SaveFileAchievementProvider : IAchievementProvider
{
    /// <summary>The key definitions use to select this provider.</summary>
    public const string ProviderKey = "save-file";

    private readonly ISaveFileReader _reader;
    private readonly ILogger<SaveFileAchievementProvider> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="reader">Extracts values from save files.</param>
    /// <param name="logger">Logger for evaluation diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SaveFileAchievementProvider(ISaveFileReader reader, ILogger<SaveFileAchievementProvider> logger)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => ProviderKey;

    /// <inheritdoc />
    public string DisplayName => "Save file";

    /// <inheritdoc />
    /// <remarks>
    /// Evaluated when the game exits and when a watched file changes — the two
    /// moments a save can actually have been written. Excluded from the running
    /// poll, which would re-read the same file several times a second for nothing.
    /// </remarks>
    public bool HandlesTrigger(AchievementTrigger trigger) =>
        trigger is AchievementTrigger.GameExited
            or AchievementTrigger.SaveFileChanged
            or AchievementTrigger.Startup
            or AchievementTrigger.Manual;

    /// <inheritdoc />
    public Task<IReadOnlyList<AchievementEvaluation>> EvaluateAsync(
        IReadOnlyList<AchievementDefinition> definitions,
        AchievementEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var results = new List<AchievementEvaluation>(definitions.Count);

        // Values are cached per file within the batch, so ten rules against one
        // save file cost one read rather than ten.
        var cache = new Dictionary<(string Path, SaveFileFormat Format, string Field), SaveFileReadResult>();

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = SaveFileTriggerConfig.TryParse(definition.TriggerConfigJson);

            if (config is null)
            {
                results.Add(AchievementEvaluation.Unavailable(
                    definition.Id, "The save file trigger configuration could not be read."));
                continue;
            }

            if (!config.Validate(out var configError))
            {
                results.Add(AchievementEvaluation.Unavailable(definition.Id, configError!));
                continue;
            }

            var key = (config.SaveFilePath, config.Format, config.FieldPath);

            if (!cache.TryGetValue(key, out var read))
            {
                read = _reader.ReadValue(config.SaveFilePath, config.Format, config.FieldPath);
                cache[key] = read;
            }

            if (!read.Found)
            {
                // A save file that does not exist yet is the normal state before
                // the player saves, so this is a diagnostic rather than a fault.
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
            "Save file provider evaluated {Count} rules across {Files} distinct reads.",
            definitions.Count, cache.Count);

        return Task.FromResult<IReadOnlyList<AchievementEvaluation>>(results);
    }
}
