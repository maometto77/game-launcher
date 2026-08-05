using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Launcher;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One thing an achievement can belong to: a title, or the library as a whole.
/// </summary>
/// <param name="CatalogId">Catalog identity, or <see langword="null"/> for library-wide.</param>
/// <param name="Title">Name shown in the picker.</param>
/// <param name="GameId">
/// Local game row, when one is installed. Used only to find a running process for
/// a test read.
/// </param>
public sealed record AchievementTarget(string? CatalogId, string Title, int? GameId);

/// <summary>
/// View model for authoring or editing a single achievement definition.
/// </summary>
/// <remarks>
/// <para>
/// Definition-focused. Everything here describes what an achievement <em>is</em>
/// and what would have to be true to earn it. Nothing here awards one: the only
/// evaluation this dialog can reach is
/// <see cref="IAchievementEngine.TestAsync"/>, which asks a provider for its
/// verdict and returns it without writing an unlock, touching progress, or
/// raising an unlock event.
/// </para>
/// <para>
/// The provider list comes from the engine, so a definition can only be authored
/// against a provider that is actually installed. A definition already naming a
/// provider that has since been removed keeps that key — rewriting it to
/// something installed would silently change what the achievement means.
/// </para>
/// </remarks>
public sealed partial class AchievementEditorViewModel : DialogViewModelBase
{
    private readonly IAchievementRepository _achievements;
    private readonly IGameRepository _games;
    private readonly ICollectionRepository _collections;
    private readonly IAchievementEngine _engine;
    private readonly IGameLaunchService _launcher;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AchievementEditorViewModel> _logger;

    /// <summary>The definition being edited, or <see langword="null"/> when authoring a new one.</summary>
    private AchievementDefinition? _existing;

    [ObservableProperty]
    private string _dialogTitle = "New achievement";

    [ObservableProperty]
    private ObservableCollection<AchievementProviderDescriptor> _providers = [];

    [ObservableProperty]
    private AchievementProviderDescriptor? _selectedProvider;

    [ObservableProperty]
    private ObservableCollection<AchievementTarget> _targets = [];

    [ObservableProperty]
    private AchievementTarget? _selectedTarget;

    [ObservableProperty]
    private ObservableCollection<Collection> _collectionOptions = [];

    [ObservableProperty]
    private Collection? _selectedCollection;

    [ObservableProperty]
    private string _apiName = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isHidden;

    [ObservableProperty]
    private string _sortOrderText = "0";

    [ObservableProperty]
    private string _progressTargetText = string.Empty;

    // ---- Meta rule ----

    [ObservableProperty]
    private MetaMetric _metaMetric = MetaMetric.FirstLaunch;

    [ObservableProperty]
    private string _metaThresholdText = "1";

    // ---- Save-file rule ----

    [ObservableProperty]
    private string _saveFilePath = string.Empty;

    [ObservableProperty]
    private SaveFileFormat _saveFileFormat = SaveFileFormat.Json;

    [ObservableProperty]
    private string _fieldPath = string.Empty;

    // ---- Memory rule ----

    [ObservableProperty]
    private string _moduleName = string.Empty;

    [ObservableProperty]
    private string _offset = "0x0";

    [ObservableProperty]
    private MemoryValueType _valueType = MemoryValueType.Int32;

    // ---- Shared by the save-file and memory rules ----

    [ObservableProperty]
    private ComparisonOperator _comparison = ComparisonOperator.GreaterThanOrEqual;

    [ObservableProperty]
    private string _targetValue = string.Empty;

    // ---- Test read ----

    [ObservableProperty]
    private string? _testResult;

    [ObservableProperty]
    private bool _testSucceeded;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string? _missingProviderKey;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="achievements">Achievement persistence.</param>
    /// <param name="games">Supplies the list of titles an achievement can belong to.</param>
    /// <param name="collections">Supplies collections for the collection-completion metric.</param>
    /// <param name="engine">Supplies the provider list and the non-persisting test read.</param>
    /// <param name="launcher">Finds a running process so a memory rule can be tested.</param>
    /// <param name="dialogs">File picker for save-file rules.</param>
    /// <param name="logger">Logger for editor diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AchievementEditorViewModel(
        IAchievementRepository achievements,
        IGameRepository games,
        ICollectionRepository collections,
        IAchievementEngine engine,
        IGameLaunchService launcher,
        IDialogService dialogs,
        ILogger<AchievementEditorViewModel> logger)
    {
        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Providers = new ObservableCollection<AchievementProviderDescriptor>(_engine.Providers);
    }

    /// <summary>Gets the available comparison operators.</summary>
    public IReadOnlyList<ComparisonOperator> ComparisonOptions { get; } =
        Enum.GetValues<ComparisonOperator>();

    /// <summary>Gets the available save-file formats.</summary>
    public IReadOnlyList<SaveFileFormat> SaveFileFormatOptions { get; } =
        Enum.GetValues<SaveFileFormat>();

    /// <summary>Gets the available memory value types.</summary>
    public IReadOnlyList<MemoryValueType> ValueTypeOptions { get; } =
        Enum.GetValues<MemoryValueType>();

    /// <summary>Gets the available meta metrics.</summary>
    public IReadOnlyList<MetaMetric> MetaMetricOptions { get; } = Enum.GetValues<MetaMetric>();

    /// <summary>Gets a value indicating whether a meta rule is being edited.</summary>
    public bool IsMetaRule => IsProvider(MetaAchievementProvider.ProviderKey);

    /// <summary>Gets a value indicating whether a save-file rule is being edited.</summary>
    public bool IsSaveFileRule => IsProvider(SaveFileAchievementProvider.ProviderKey);

    /// <summary>Gets a value indicating whether a memory rule is being edited.</summary>
    public bool IsMemoryRule => IsProvider(MemoryAchievementProvider.ProviderKey);

    /// <summary>Gets a value indicating whether the shared comparison fields apply.</summary>
    public bool HasComparison => IsSaveFileRule || IsMemoryRule;

    /// <summary>Gets a value indicating whether the collection picker applies.</summary>
    public bool NeedsCollection => IsMetaRule && MetaMetric == MetaMetric.CollectionCompletion;

    /// <summary>
    /// Gets a value indicating whether this definition names a provider that is
    /// not installed.
    /// </summary>
    public bool IsProviderMissing => MissingProviderKey is not null;

    /// <summary>Gets a value indicating whether a test result is being shown.</summary>
    public bool HasTestResult => !string.IsNullOrWhiteSpace(TestResult);

    /// <summary>
    /// Prepares the editor for a definition, or for a new one.
    /// </summary>
    /// <param name="definition">
    /// The definition to edit, or <see langword="null"/> to author a new one.
    /// </param>
    /// <remarks>
    /// Takes a snapshot rather than binding to the caller's instance, so
    /// cancelling leaves the object the achievements page is still showing
    /// untouched.
    /// </remarks>
    public void Initialize(AchievementDefinition? definition)
    {
        _existing = definition;

        if (definition is null)
        {
            DialogTitle = "New achievement";
            SelectedProvider = Providers.FirstOrDefault(
                provider => provider.Key == MetaAchievementProvider.ProviderKey) ?? Providers.FirstOrDefault();
            return;
        }

        DialogTitle = "Edit achievement";

        ApiName = definition.ApiName;
        Title = definition.Title;
        Description = definition.Description;
        IsHidden = definition.IsHidden;
        SortOrderText = definition.SortOrder.ToString(CultureInfo.CurrentCulture);
        ProgressTargetText = definition.ProgressTarget?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;

        SelectedProvider = Providers.FirstOrDefault(
            provider => string.Equals(provider.Key, definition.ProviderKey, StringComparison.OrdinalIgnoreCase));

        // The key is preserved even when nothing can evaluate it. Silently
        // reassigning it to an installed provider would change what the
        // achievement means without anybody asking for that.
        MissingProviderKey = SelectedProvider is null && !string.IsNullOrWhiteSpace(definition.ProviderKey)
            ? definition.ProviderKey
            : null;

        LoadRule(definition);
    }

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var games = await _games.GetAllAsync(cancellationToken).ConfigureAwait(true);

            var targets = new List<AchievementTarget>
            {
                new(null, AchievementGroupViewModel.LibraryWideTitle, null)
            };

            // One entry per catalog identity: two installations of the same title
            // share achievements, so offering both would imply otherwise.
            foreach (var game in games
                         .Where(game => !string.IsNullOrWhiteSpace(game.CatalogId))
                         .GroupBy(game => game.CatalogId!, StringComparer.Ordinal)
                         .Select(group => group.First())
                         .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                targets.Add(new AchievementTarget(game.CatalogId, game.Title, game.Id));
            }

            Targets = new ObservableCollection<AchievementTarget>(targets);

            SelectedTarget = targets.FirstOrDefault(target =>
                                 string.Equals(target.CatalogId, _existing?.CatalogId, StringComparison.Ordinal))
                             ?? targets[0];

            var collections = await _collections.GetAllAsync(cancellationToken).ConfigureAwait(true);
            CollectionOptions = new ObservableCollection<Collection>(collections);

            if (MetaTriggerConfig.TryParse(_existing?.TriggerConfigJson)?.CollectionId is { } collectionId)
            {
                SelectedCollection = collections.FirstOrDefault(candidate => candidate.Id == collectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the achievement editor failed.");
            SetErrorMessage("The list of games could not be loaded.");
        }
    }

    /// <summary>
    /// Reads a definition's stored rule into the matching fields.
    /// </summary>
    /// <param name="definition">The definition being edited.</param>
    /// <remarks>
    /// A rule that cannot be parsed leaves the fields at their defaults and is
    /// reported rather than throwing, so a hand-edited definition can still be
    /// opened and repaired.
    /// </remarks>
    private void LoadRule(AchievementDefinition definition)
    {
        switch (definition.ProviderKey)
        {
            case MetaAchievementProvider.ProviderKey:
                if (MetaTriggerConfig.TryParse(definition.TriggerConfigJson) is { } meta)
                {
                    MetaMetric = meta.Metric;
                    MetaThresholdText = meta.Threshold.ToString("0.##", CultureInfo.CurrentCulture);
                }
                else
                {
                    SetErrorMessage("This achievement's rule could not be read, so the fields show defaults.");
                }

                break;

            case SaveFileAchievementProvider.ProviderKey:
                if (SaveFileTriggerConfig.TryParse(definition.TriggerConfigJson) is { } save)
                {
                    SaveFilePath = save.SaveFilePath;
                    SaveFileFormat = save.Format;
                    FieldPath = save.FieldPath;
                    Comparison = save.Comparison;
                    TargetValue = save.Value;
                }
                else
                {
                    SetErrorMessage("This achievement's rule could not be read, so the fields show defaults.");
                }

                break;

            case MemoryAchievementProvider.ProviderKey:
                if (MemoryTriggerConfig.TryParse(definition.TriggerConfigJson) is { } memory)
                {
                    ModuleName = memory.ModuleName;
                    Offset = memory.Offset;
                    ValueType = memory.ValueType;
                    Comparison = memory.Comparison;
                    TargetValue = memory.Value;
                }
                else
                {
                    SetErrorMessage("This achievement's rule could not be read, so the fields show defaults.");
                }

                break;
        }
    }

    /// <summary>
    /// Builds a definition from the current form without persisting it.
    /// </summary>
    /// <returns>
    /// A definition carrying the edited values, or <see langword="null"/> when the
    /// form is not valid.
    /// </returns>
    /// <remarks>
    /// Used by both saving and testing. The instance returned for a test is a copy
    /// carrying the edited rule, never the stored definition, so a test always
    /// reflects what is on screen rather than what was last written.
    /// </remarks>
    private AchievementDefinition? BuildDefinition()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            SetErrorMessage("A title is required.");
            return null;
        }

        var providerKey = SelectedProvider?.Key ?? MissingProviderKey;

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            SetErrorMessage("Choose how this achievement is evaluated.");
            return null;
        }

        if (!TryBuildRule(providerKey, out var triggerJson))
        {
            return null;
        }

        double? progressTarget = null;

        if (!string.IsNullOrWhiteSpace(ProgressTargetText))
        {
            if (!double.TryParse(ProgressTargetText, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed))
            {
                SetErrorMessage($"'{ProgressTargetText}' is not a number.");
                return null;
            }

            progressTarget = parsed;
        }

        _ = int.TryParse(SortOrderText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var sortOrder);

        return new AchievementDefinition
        {
            Id = _existing?.Id ?? 0,
            GlobalKey = _existing?.GlobalKey ?? string.Empty,
            CatalogId = SelectedTarget?.CatalogId,
            ApiName = ApiName.Trim(),
            Title = Title.Trim(),
            Description = Description.Trim(),
            IconPath = _existing?.IconPath,
            Kind = ResolveKind(providerKey),
            ProviderKey = providerKey,
            TriggerConfigJson = triggerJson,
            IsHidden = IsHidden,
            SortOrder = sortOrder,
            ProgressTarget = progressTarget,
            StatApiName = _existing?.StatApiName
        };
    }

    /// <summary>
    /// Serialises the rule fields for the selected provider.
    /// </summary>
    /// <param name="providerKey">The provider the rule is authored for.</param>
    /// <param name="json">Receives the serialised rule.</param>
    /// <returns><see langword="true"/> when the rule is complete and valid.</returns>
    private bool TryBuildRule(string providerKey, out string json)
    {
        json = "{}";

        switch (providerKey)
        {
            case MetaAchievementProvider.ProviderKey:
            {
                if (!double.TryParse(
                        MetaThresholdText, NumberStyles.Float, CultureInfo.CurrentCulture, out var threshold))
                {
                    SetErrorMessage($"'{MetaThresholdText}' is not a number.");
                    return false;
                }

                if (MetaMetric == MetaMetric.CollectionCompletion && SelectedCollection is null)
                {
                    SetErrorMessage("Choose the collection this achievement measures.");
                    return false;
                }

                json = JsonSerializer.Serialize(new MetaTriggerConfig
                {
                    Metric = MetaMetric,
                    Threshold = threshold,
                    CollectionId = MetaMetric == MetaMetric.CollectionCompletion ? SelectedCollection?.Id : null
                });

                return true;
            }

            case SaveFileAchievementProvider.ProviderKey:
            {
                var config = new SaveFileTriggerConfig
                {
                    SaveFilePath = SaveFilePath.Trim(),
                    Format = SaveFileFormat,
                    FieldPath = FieldPath.Trim(),
                    Comparison = Comparison,
                    Value = TargetValue.Trim()
                };

                if (!config.Validate(out var error))
                {
                    SetErrorMessage(error);
                    return false;
                }

                json = JsonSerializer.Serialize(config);
                return true;
            }

            case MemoryAchievementProvider.ProviderKey:
            {
                var config = new MemoryTriggerConfig
                {
                    ModuleName = ModuleName.Trim(),
                    Offset = Offset.Trim(),
                    ValueType = ValueType,
                    Comparison = Comparison,
                    Value = TargetValue.Trim()
                };

                if (!config.Validate(out var error))
                {
                    SetErrorMessage(error);
                    return false;
                }

                json = JsonSerializer.Serialize(config);
                return true;
            }

            default:
                // A provider with no rule shape the editor knows — manual, or one
                // added later. Its stored configuration is carried through
                // untouched rather than replaced with an empty object.
                json = _existing?.TriggerConfigJson ?? "{}";
                return true;
        }
    }

    /// <summary>
    /// Chooses the display category for a provider key.
    /// </summary>
    /// <param name="providerKey">The provider the definition is authored for.</param>
    /// <returns>The category to store.</returns>
    /// <remarks>
    /// <see cref="AchievementDefinition.Kind"/> groups the list; the provider key
    /// decides evaluation. An existing definition keeps whatever category it was
    /// given, because a custom provider may legitimately have chosen one that does
    /// not follow from its key.
    /// </remarks>
    private AchievementKind ResolveKind(string providerKey) => providerKey switch
    {
        MetaAchievementProvider.ProviderKey => AchievementKind.Meta,
        SaveFileAchievementProvider.ProviderKey => AchievementKind.SaveFile,
        MemoryAchievementProvider.ProviderKey => AchievementKind.Memory,
        _ => _existing?.Kind ?? AchievementKind.Meta
    };

    /// <summary>
    /// Asks the provider what it currently sees, without recording anything.
    /// </summary>
    /// <returns>A task that completes when the verdict has been reported.</returns>
    /// <remarks>
    /// The whole point of this command is that it is inert. It calls
    /// <see cref="IAchievementEngine.TestAsync"/>, which reaches the provider and
    /// nothing else: no unlock row, no progress row, no unlock event. Somebody
    /// checking whether an offset is right must not thereby award themselves the
    /// achievement.
    /// </remarks>
    [RelayCommand]
    private async Task TestAsync()
    {
        ClearError();
        TestResult = null;
        OnPropertyChanged(nameof(HasTestResult));

        var providerKey = SelectedProvider?.Key ?? MissingProviderKey;

        if (!_engine.IsProviderAvailable(providerKey))
        {
            ReportTest(false, $"No '{providerKey}' provider is installed, so this rule cannot be evaluated.");
            return;
        }

        var candidate = BuildDefinition();

        if (candidate is null)
        {
            return;
        }

        IsTesting = true;

        try
        {
            var game = SelectedTarget?.GameId is { } gameId
                ? await _games.GetByIdAsync(gameId).ConfigureAwait(true)
                : null;

            // A memory rule can only be read from a live process, so the running
            // game's identifier is supplied when there is one. When there is not,
            // the provider says so and that is the useful answer.
            var processId = SelectedTarget?.GameId is { } id ? _launcher.GetProcessId(id) : null;

            var verdict = await _engine.TestAsync(candidate, game, processId).ConfigureAwait(true);

            if (verdict is null)
            {
                ReportTest(false, "This provider does not evaluate rules, so there is nothing to read.");
                return;
            }

            if (verdict.Diagnostic is { } diagnostic)
            {
                ReportTest(false, diagnostic);
                return;
            }

            var observed = verdict.Progress is { } progress
                ? $" Observed value: {progress.ToString("0.##", CultureInfo.CurrentCulture)}."
                : string.Empty;

            ReportTest(
                verdict.ShouldUnlock,
                verdict.ShouldUnlock
                    ? $"The condition is met.{observed} Nothing has been unlocked — this was a test."
                    : $"The condition is not met yet.{observed}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Testing achievement rule failed.");
            ReportTest(false, $"The rule could not be tested: {ex.Message}");
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Chooses the save file a save-file rule reads.</summary>
    [RelayCommand]
    private void BrowseSaveFile()
    {
        var picked = _dialogs.PickFile(
            "Select the save file",
            "Save files|*.json;*.xml;*.ini;*.sav;*.dat|All files|*.*");

        if (!string.IsNullOrWhiteSpace(picked))
        {
            SaveFilePath = picked;
        }
    }

    /// <summary>Validates and persists the definition.</summary>
    /// <returns>A task that completes once the definition has been stored.</returns>
    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearError();

        var definition = BuildDefinition();

        if (definition is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            if (_existing is null)
            {
                await _achievements.AddDefinitionAsync(definition).ConfigureAwait(true);
            }
            else
            {
                await _achievements.UpdateDefinitionAsync(definition).ConfigureAwait(true);
            }

            RequestClose(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving achievement '{Title}' failed.", definition.Title);
            SetErrorMessage($"This achievement could not be saved: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Closes the dialog without saving.</summary>
    [RelayCommand]
    private void Cancel() => RequestClose(false);

    /// <summary>Publishes a test verdict for display.</summary>
    /// <param name="succeeded">Whether the rule evaluated cleanly and its condition holds.</param>
    /// <param name="message">What to tell the user.</param>
    private void ReportTest(bool succeeded, string message)
    {
        TestSucceeded = succeeded;
        TestResult = message;
        OnPropertyChanged(nameof(HasTestResult));
    }

    /// <summary>Determines whether the selected provider has a given key.</summary>
    /// <param name="key">The provider key to compare against.</param>
    /// <returns><see langword="true"/> when the selection matches.</returns>
    private bool IsProvider(string key) =>
        string.Equals(SelectedProvider?.Key, key, StringComparison.OrdinalIgnoreCase);

    /// <summary>Refreshes the rule panels when the provider changes.</summary>
    /// <param name="value">The newly selected provider.</param>
    partial void OnSelectedProviderChanged(AchievementProviderDescriptor? value)
    {
        OnPropertyChanged(nameof(IsMetaRule));
        OnPropertyChanged(nameof(IsSaveFileRule));
        OnPropertyChanged(nameof(IsMemoryRule));
        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(NeedsCollection));

        // Choosing an installed provider is the one action that resolves a
        // missing-provider definition, so the warning clears with it.
        if (value is not null)
        {
            MissingProviderKey = null;
        }
    }

    /// <summary>Shows or hides the collection picker with the metric.</summary>
    /// <param name="value">The newly selected metric.</param>
    partial void OnMetaMetricChanged(MetaMetric value) => OnPropertyChanged(nameof(NeedsCollection));

    /// <summary>Keeps the missing-provider banner in step.</summary>
    /// <param name="value">The unresolved provider key, if any.</param>
    partial void OnMissingProviderKeyChanged(string? value) => OnPropertyChanged(nameof(IsProviderMissing));
}
