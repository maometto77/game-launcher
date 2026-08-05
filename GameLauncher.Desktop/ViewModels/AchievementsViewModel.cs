using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Dialogs;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the library-wide achievements page.
/// </summary>
/// <remarks>
/// <para>
/// Reads and presents; it never evaluates. Deciding whether an achievement has
/// been earned belongs to the providers, and scheduling that decision belongs to
/// the watcher — so this page has no way to unlock anything, by construction. The
/// engine is used solely to ask which providers are installed, which is metadata
/// rather than evaluation.
/// </para>
/// <para>
/// Everything shown comes from three reads: the definitions, the unlocks, and the
/// recorded progress. Filtering happens over the loaded rows, so changing it
/// touches neither the database nor the engine.
/// </para>
/// </remarks>
public sealed partial class AchievementsViewModel : ViewModelBase
{
    private readonly IAchievementRepository _achievements;
    private readonly IGameRepository _games;
    private readonly IAchievementEngine _engine;
    private readonly IWindowService _windows;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AchievementsViewModel> _logger;

    /// <summary>Every group, before the filter is applied.</summary>
    private IReadOnlyList<LoadedGroup> _loaded = [];

    [ObservableProperty]
    private ObservableCollection<AchievementGroupViewModel> _groups = [];

    [ObservableProperty]
    private AchievementFilter _filter = AchievementFilter.All;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private bool _hasAny;

    [ObservableProperty]
    private string? _providerWarning;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="achievements">Achievement persistence.</param>
    /// <param name="games">Supplies the titles the groups are named after.</param>
    /// <param name="engine">Consulted only for which providers are installed.</param>
    /// <param name="windows">Opens the achievement editor.</param>
    /// <param name="dialogs">Confirmation prompts.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AchievementsViewModel(
        IAchievementRepository achievements,
        IGameRepository games,
        IAchievementEngine engine,
        IWindowService windows,
        IDialogService dialogs,
        ILogger<AchievementsViewModel> logger)
    {
        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(cancellationToken);

    /// <summary>
    /// Loads every definition with its unlock state and progress.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A task that completes when the page is populated.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ClearError();

        try
        {
            var definitions = await _achievements.GetAllDefinitionsAsync(cancellationToken).ConfigureAwait(true);

            if (definitions.Count == 0)
            {
                _loaded = [];
                Groups = [];
                HasAny = false;
                SummaryText = "No achievements have been configured yet.";
                ProviderWarning = null;
                return;
            }

            var unlocks = await _achievements.GetUnlocksAsync(cancellationToken).ConfigureAwait(true);
            var unlockTimes = unlocks.ToDictionary(unlock => unlock.DefinitionId, unlock => unlock.UnlockedAt);

            var progress = await _achievements
                .GetProgressAsync(definitions.Select(definition => definition.Id).ToArray(), cancellationToken)
                .ConfigureAwait(true);

            var titles = await LoadCatalogTitlesAsync(cancellationToken).ConfigureAwait(true);

            _loaded = BuildGroups(definitions, unlockTimes, progress, titles);

            HasAny = true;
            ApplyFilter();

            var total = definitions.Count;
            var unlocked = definitions.Count(definition => unlockTimes.ContainsKey(definition.Id));
            SummaryText = $"{unlocked} of {total} unlocked across {_loaded.Count} " +
                          (_loaded.Count == 1 ? "title" : "titles");

            var inert = definitions.Count(definition => !_engine.IsProviderAvailable(definition.ProviderKey));
            ProviderWarning = inert == 0
                ? null
                : $"{inert} achievement{(inert == 1 ? "" : "s")} name a provider that is not installed " +
                  "and will never be evaluated.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the achievements page failed.");
            SetErrorMessage("Your achievements could not be loaded.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Maps catalog identities to the title they should be listed under.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Display titles keyed by catalog identity.</returns>
    /// <remarks>
    /// Taken from the library because a catalog entry's canonical title is not
    /// what the user named the game. Where two installations share a catalog
    /// identity the first is used; they are the same title by definition.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> LoadCatalogTitlesAsync(
        CancellationToken cancellationToken)
    {
        var games = await _games.GetAllAsync(cancellationToken).ConfigureAwait(true);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var game in games)
        {
            if (!string.IsNullOrWhiteSpace(game.CatalogId))
            {
                titles.TryAdd(game.CatalogId, game.Title);
            }
        }

        return titles;
    }

    /// <summary>
    /// Projects definitions into groups, one per catalog identity.
    /// </summary>
    /// <param name="definitions">Every definition in the library.</param>
    /// <param name="unlockTimes">Unlock timestamps keyed by definition.</param>
    /// <param name="progress">Recorded progress keyed by definition.</param>
    /// <param name="titles">Display titles keyed by catalog identity.</param>
    /// <returns>The groups, ordered with library-wide achievements last.</returns>
    private IReadOnlyList<LoadedGroup> BuildGroups(
        IReadOnlyList<AchievementDefinition> definitions,
        IReadOnlyDictionary<int, DateTimeOffset> unlockTimes,
        IReadOnlyDictionary<int, AchievementProgress> progress,
        IReadOnlyDictionary<string, string> titles)
    {
        return definitions
            .GroupBy(definition => definition.CatalogId, StringComparer.Ordinal)
            .Select(group =>
            {
                var catalogId = group.Key;

                var title = catalogId is null
                    ? AchievementGroupViewModel.LibraryWideTitle
                    : titles.TryGetValue(catalogId, out var known)
                        ? known

                        // Catalogued but not installed: the achievements survive an
                        // uninstall, so the group has to survive with them.
                        : "No longer installed";

                var items = group
                    .Select(definition => new AchievementItemViewModel(
                        definition,
                        unlockTimes.TryGetValue(definition.Id, out var stamp) ? stamp : null,
                        progress.TryGetValue(definition.Id, out var recorded) ? recorded.CurrentValue : null,
                        _engine.IsProviderAvailable(definition.ProviderKey)))

                    // Earned first, then in the order the set was authored, so a
                    // player sees what they have before what they have not.
                    .OrderByDescending(item => item.IsUnlocked)
                    .ThenBy(item => item.Definition.SortOrder)
                    .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                return new LoadedGroup(
                    title,
                    catalogId,
                    items,
                    items.Count(item => item.IsUnlocked));
            })

            // Library-wide achievements sort last: they belong to no title, and
            // leading with them would push the player's actual games down.
            .OrderBy(group => group.CatalogId is null)
            .ThenBy(group => group.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Rebuilds the visible groups from the loaded rows for the current filter.
    /// </summary>
    private void ApplyFilter()
    {
        var visible = _loaded
            .Select(group => new AchievementGroupViewModel(
                group.Title,
                group.CatalogId,
                Filter switch
                {
                    AchievementFilter.Unlocked => group.Items.Where(item => item.IsUnlocked).ToList(),
                    AchievementFilter.Locked => group.Items.Where(item => !item.IsUnlocked).ToList(),
                    _ => group.Items
                },
                group.Items.Count,
                group.UnlockedCount))

            // A group with nothing left to show under the current filter is
            // dropped rather than rendered as an empty heading.
            .Where(group => group.Items.Count > 0)
            .ToList();

        Groups = new ObservableCollection<AchievementGroupViewModel>(visible);
    }

    /// <summary>Rebuilds the list when the filter changes.</summary>
    /// <param name="value">The newly selected filter.</param>
    partial void OnFilterChanged(AchievementFilter value) => ApplyFilter();

    /// <summary>Reloads the page from storage.</summary>
    /// <returns>A task that completes when the reload has finished.</returns>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(CancellationToken.None);

    /// <summary>Opens the editor to author a new achievement.</summary>
    /// <returns>A task that completes once the editor has closed and the page reloaded.</returns>
    [RelayCommand]
    private async Task NewAchievementAsync()
    {
        if (_windows.ShowDialogFor<AchievementEditorViewModel>(editor => editor.Initialize(null)) == true)
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Opens the editor for an existing achievement.</summary>
    /// <param name="item">The achievement to edit.</param>
    /// <returns>A task that completes once the editor has closed and the page reloaded.</returns>
    [RelayCommand]
    private async Task EditAsync(AchievementItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (_windows.ShowDialogFor<AchievementEditorViewModel>(
                editor => editor.Initialize(item.Definition)) == true)
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Deletes an achievement definition.</summary>
    /// <param name="item">The achievement to delete.</param>
    /// <returns>A task that completes once the definition has been removed.</returns>
    [RelayCommand]
    private async Task DeleteAsync(AchievementItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // Said plainly, because deleting a definition takes the record of having
        // earned it with it. Nothing else in the launcher discards an unlock.
        var warning = item.IsUnlocked
            ? $"Delete '{item.Title}'?\n\nThe record of having unlocked it will be deleted too."
            : $"Delete '{item.Title}'?";

        if (!_dialogs.Confirm("Delete achievement", warning, isDestructive: true))
        {
            return;
        }

        try
        {
            await _achievements.DeleteDefinitionAsync(item.Id).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting achievement {Id} failed.", item.Id);
            SetErrorMessage($"'{item.Title}' could not be deleted: {ex.Message}");
        }
    }

    /// <summary>
    /// One group as loaded, holding the full item list the filter draws from.
    /// </summary>
    /// <param name="Title">Heading for the group.</param>
    /// <param name="CatalogId">Catalog identity, or <see langword="null"/> for library-wide.</param>
    /// <param name="Items">Every achievement in the group.</param>
    /// <param name="UnlockedCount">How many have been earned.</param>
    private sealed record LoadedGroup(
        string Title,
        string? CatalogId,
        IReadOnlyList<AchievementItemViewModel> Items,
        int UnlockedCount);
}
