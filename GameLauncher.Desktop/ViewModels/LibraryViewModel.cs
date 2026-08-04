using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Infrastructure.Navigation;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the Library page: the full game list with search, sort,
/// collection filtering and a grid/list presentation toggle.
/// </summary>
/// <remarks>
/// The complete game list is loaded once and held in memory; search, sort and
/// filter are then applied in-process rather than by re-querying. A personal
/// library is a few hundred rows at most, so a round trip per keystroke would
/// cost latency without buying anything.
/// </remarks>
public sealed partial class LibraryViewModel : ViewModelBase
{
    private readonly IGameRepository _games;
    private readonly ICollectionRepository _collections;
    private readonly INavigationService _navigation;
    private readonly IWindowService _windows;
    private readonly ILogger<LibraryViewModel> _logger;

    private IReadOnlyList<Game> _allGames = [];
    private IReadOnlyDictionary<int, string> _collectionNames = new Dictionary<int, string>();

    [ObservableProperty]
    private ObservableCollection<GameItemViewModel> _visibleGames = [];

    [ObservableProperty]
    private ObservableCollection<CollectionFilter> _collectionFilters = [];

    [ObservableProperty]
    private LibraryViewMode _viewMode = LibraryViewMode.Grid;

    [ObservableProperty]
    private LibrarySortOrder _sortOrder = LibrarySortOrder.Title;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CollectionFilter? _selectedCollectionFilter;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="games">Game persistence.</param>
    /// <param name="collections">Collection persistence.</param>
    /// <param name="navigation">Used to open a game's details page.</param>
    /// <param name="windows">Opens the Add Game and Scan Folder dialogs.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public LibraryViewModel(
        IGameRepository games,
        ICollectionRepository collections,
        INavigationService navigation,
        IWindowService windows,
        ILogger<LibraryViewModel> logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the sort orders offered by the toolbar.</summary>
    public IReadOnlyList<LibrarySortOrder> SortOrders { get; } =
        Enum.GetValues<LibrarySortOrder>();

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Reloads the library from storage and reapplies the current filters.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A task that completes when the page is populated.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ClearError();

        try
        {
            var games = await _games.GetAllAsync(cancellationToken).ConfigureAwait(true);
            var collections = await _collections.GetAllAsync(cancellationToken).ConfigureAwait(true);
            var counts = await _collections.GetGameCountsAsync(cancellationToken).ConfigureAwait(true);

            _allGames = games;
            _collectionNames = collections.ToDictionary(
                collection => collection.Id,
                collection => collection.Name);

            RebuildCollectionFilters(collections, counts, games.Count);
            ApplyFilters();

            _logger.LogDebug("Library loaded with {Count} games.", games.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the library failed.");
            SetErrorMessage("The library could not be loaded. See the log for details.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the details page for a game.</summary>
    /// <param name="item">The game to open.</param>
    /// <returns>A task that completes when navigation finishes.</returns>
    [RelayCommand]
    private async Task OpenGameAsync(GameItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _navigation
                .NavigateToAsync<GameDetailsViewModel, int>(item.Id)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening game {GameId} failed.", item.Id);
            SetErrorMessage($"Could not open {item.Title}.");
        }
    }

    /// <summary>Opens the Add Game dialog and reloads if a game was added.</summary>
    /// <returns>A task that completes once the dialog has closed and any reload has finished.</returns>
    [RelayCommand]
    private async Task AddGameAsync()
    {
        if (_windows.ShowDialogFor<AddGameViewModel>() == true)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Opens the Scan Folder dialog and reloads if anything was added.</summary>
    /// <returns>A task that completes once the dialog has closed and any reload has finished.</returns>
    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        if (_windows.ShowDialogFor<ScanFolderViewModel>() == true)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Opens the Install from URL dialog and reloads if a game was added.</summary>
    /// <returns>A task that completes once the dialog has closed and any reload has finished.</returns>
    [RelayCommand]
    private async Task InstallFromUrlAsync()
    {
        if (_windows.ShowDialogFor<InstallFromUrlViewModel>() == true)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Switches the library between grid and list presentation.</summary>
    /// <param name="mode">The presentation to switch to.</param>
    [RelayCommand]
    private void SetViewMode(LibraryViewMode mode) => ViewMode = mode;

    /// <summary>Clears the current search text.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>Reapplies filters when the search text changes.</summary>
    /// <param name="value">The new search text.</param>
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    /// <summary>Reapplies filters when the sort order changes.</summary>
    /// <param name="value">The new sort order.</param>
    partial void OnSortOrderChanged(LibrarySortOrder value) => ApplyFilters();

    /// <summary>Reapplies filters when the collection filter changes.</summary>
    /// <param name="value">The newly selected filter.</param>
    partial void OnSelectedCollectionFilterChanged(CollectionFilter? value) => ApplyFilters();

    /// <summary>
    /// Rebuilds the collection filter list, preserving the user's current
    /// selection across reloads where possible.
    /// </summary>
    /// <param name="collections">All known collections.</param>
    /// <param name="counts">Game counts keyed by collection identifier.</param>
    /// <param name="totalGames">Total number of games in the library.</param>
    private void RebuildCollectionFilters(
        IReadOnlyList<Collection> collections,
        IReadOnlyDictionary<int, int> counts,
        int totalGames)
    {
        var previousId = SelectedCollectionFilter?.CollectionId;

        var filters = new ObservableCollection<CollectionFilter>
        {
            new(null, "All games", totalGames)
        };

        foreach (var collection in collections)
        {
            filters.Add(new CollectionFilter(
                collection.Id,
                collection.Name,
                counts.TryGetValue(collection.Id, out var count) ? count : 0));
        }

        CollectionFilters = filters;

        // Reassigning the collection resets selection, so restore it explicitly.
        // Falling back to "All games" covers a collection deleted since last load.
        SelectedCollectionFilter =
            filters.FirstOrDefault(filter => filter.CollectionId == previousId) ?? filters[0];
    }

    /// <summary>
    /// Applies the search text, collection filter and sort order to the loaded
    /// games and republishes the visible list.
    /// </summary>
    private void ApplyFilters()
    {
        IEnumerable<Game> query = _allGames;

        if (SelectedCollectionFilter?.CollectionId is { } collectionId)
        {
            query = query.Where(game => game.CollectionId == collectionId);
        }

        var search = SearchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            // Titles and tags are both searched, so "racing" finds a game tagged
            // Racing even when the word is nowhere in its name.
            query = query.Where(game =>
                game.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || game.Tags.Any(tag => tag.Contains(search, StringComparison.CurrentCultureIgnoreCase)));
        }

        query = SortOrder switch
        {
            LibrarySortOrder.Title =>
                query.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),

            // Never-played games have no LastPlayedAt; they sort last rather than
            // first, which is what a descending sort on null would otherwise do.
            LibrarySortOrder.LastPlayed =>
                query.OrderByDescending(game => game.LastPlayedAt ?? DateTimeOffset.MinValue)
                     .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),

            LibrarySortOrder.Playtime =>
                query.OrderByDescending(game => game.PlaytimeSeconds)
                     .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),

            LibrarySortOrder.DateAdded =>
                query.OrderByDescending(game => game.DateAdded)
                     .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),

            LibrarySortOrder.InstallSize =>
                query.OrderByDescending(game => game.InstallSizeBytes)
                     .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),

            _ => query.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
        };

        var items = query
            .Select(game => new GameItemViewModel(
                game,
                game.CollectionId is { } id && _collectionNames.TryGetValue(id, out var name) ? name : null))
            .ToList();

        VisibleGames = new ObservableCollection<GameItemViewModel>(items);
        IsEmpty = items.Count == 0;

        ResultSummary = _allGames.Count == items.Count
            ? $"{items.Count} game{(items.Count == 1 ? string.Empty : "s")}"
            : $"{items.Count} of {_allGames.Count} games";
    }

    /// <summary>
    /// One entry in the collection filter dropdown.
    /// </summary>
    /// <param name="CollectionId">
    /// Identifier of the collection, or <see langword="null"/> for "All games".
    /// </param>
    /// <param name="Name">Display name.</param>
    /// <param name="GameCount">Number of games the filter would show.</param>
    public sealed record CollectionFilter(int? CollectionId, string Name, int GameCount)
    {
        /// <summary>Gets the label shown in the dropdown, including the count.</summary>
        public string Label => $"{Name} ({GameCount})";
    }
}
