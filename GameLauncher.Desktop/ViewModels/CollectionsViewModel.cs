using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Dialogs;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// One collection as shown in the sidebar list.
/// </summary>
/// <param name="Collection">The underlying record.</param>
/// <param name="GameCount">How many games are filed under it.</param>
public sealed record CollectionListItem(Collection Collection, int GameCount)
{
    /// <summary>Gets the collection's identifier.</summary>
    public int Id => Collection.Id;

    /// <summary>Gets the collection's name.</summary>
    public string Name => Collection.Name;

    /// <summary>Gets the label shown in the list, including the game count.</summary>
    public string Label => $"{Name}  ({GameCount})";
}

/// <summary>
/// View model for the Collections page.
/// </summary>
/// <remarks>
/// A game belongs to at most one collection, so moving it into a new one
/// necessarily removes it from its previous one. That is the point of
/// collections as opposed to tags, and the page presents it that way rather than
/// pretending membership is a set.
/// </remarks>
public sealed partial class CollectionsViewModel : ViewModelBase
{
    private readonly ICollectionRepository _collections;
    private readonly IGameRepository _games;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CollectionsViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<CollectionListItem> _collectionItems = [];

    [ObservableProperty]
    private CollectionListItem? _selectedCollection;

    [ObservableProperty]
    private ObservableCollection<GameItemViewModel> _gamesInCollection = [];

    [ObservableProperty]
    private ObservableCollection<GameItemViewModel> _availableGames = [];

    [ObservableProperty]
    private GameItemViewModel? _selectedMemberGame;

    [ObservableProperty]
    private GameItemViewModel? _selectedAvailableGame;

    [ObservableProperty]
    private string _newCollectionName = string.Empty;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private string? _statusText;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="collections">Collection persistence.</param>
    /// <param name="games">Game persistence.</param>
    /// <param name="dialogs">Confirmation prompts.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CollectionsViewModel(
        ICollectionRepository collections,
        IGameRepository games,
        IDialogService dialogs,
        ILogger<CollectionsViewModel> logger)
    {
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets a value indicating whether a collection is selected.</summary>
    public bool HasSelection => SelectedCollection is not null;

    /// <inheritdoc />
    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(cancellationToken);

    /// <summary>
    /// Reloads collections and the membership of the selected one.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A task that completes when the page is populated.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ClearError();

        try
        {
            var previousId = SelectedCollection?.Id;

            var collections = await _collections.GetAllAsync(cancellationToken).ConfigureAwait(true);
            var counts = await _collections.GetGameCountsAsync(cancellationToken).ConfigureAwait(true);

            CollectionItems = new ObservableCollection<CollectionListItem>(
                collections.Select(collection => new CollectionListItem(
                    collection,
                    counts.TryGetValue(collection.Id, out var count) ? count : 0)));

            // Selection is restored across reloads so that adding a game does not
            // bounce the user back to the top of the list.
            SelectedCollection = CollectionItems.FirstOrDefault(item => item.Id == previousId)
                                 ?? CollectionItems.FirstOrDefault();

            await LoadMembershipAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading collections failed.");
            SetErrorMessage("Collections could not be loaded.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Creates a collection from the entered name.</summary>
    /// <returns>A task that completes once the collection exists.</returns>
    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = NewCollectionName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        ClearError();

        try
        {
            await _collections.AddAsync(new Collection
            {
                Name = name,
                SortOrder = CollectionItems.Count,
                DateAdded = DateTimeOffset.Now
            }).ConfigureAwait(true);

            NewCollectionName = string.Empty;
            StatusText = $"Created {name}.";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            // Raised for a duplicate name, which is a user error rather than a fault.
            SetErrorMessage(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating collection {Name} failed.", name);
            SetErrorMessage($"'{name}' could not be created.");
        }
    }

    /// <summary>Renames the selected collection.</summary>
    /// <returns>A task that completes once the rename has been stored.</returns>
    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedCollection is null)
        {
            return;
        }

        var name = RenameText.Trim();
        if (name.Length == 0 || string.Equals(name, SelectedCollection.Name, StringComparison.Ordinal))
        {
            return;
        }

        ClearError();

        try
        {
            var collection = SelectedCollection.Collection;
            collection.Name = name;

            await _collections.UpdateAsync(collection).ConfigureAwait(true);
            StatusText = "Renamed.";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            SetErrorMessage(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Renaming collection {Id} failed.", SelectedCollection.Id);
            SetErrorMessage("The collection could not be renamed.");
        }
    }

    /// <summary>Deletes the selected collection, leaving its games in the library.</summary>
    /// <returns>A task that completes once the collection has been removed.</returns>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedCollection is not { } selected)
        {
            return;
        }

        // Spelled out because "delete collection" reads like it might delete the
        // games too, and it does not.
        if (!_dialogs.Confirm(
                "Delete collection",
                $"Delete '{selected.Name}'?\n\n" +
                $"The {selected.GameCount} game(s) in it stay in your library and become uncollected.",
                isDestructive: true))
        {
            return;
        }

        try
        {
            await _collections.DeleteAsync(selected.Id).ConfigureAwait(true);
            SelectedCollection = null;
            StatusText = $"Deleted {selected.Name}.";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting collection {Id} failed.", selected.Id);
            SetErrorMessage("The collection could not be deleted.");
        }
    }

    /// <summary>Files the selected available game under the selected collection.</summary>
    /// <returns>A task that completes once the game has been moved.</returns>
    [RelayCommand]
    private async Task AddGameAsync()
    {
        if (SelectedCollection is not { } collection || SelectedAvailableGame is not { } game)
        {
            return;
        }

        await MoveAsync(game.Id, collection.Id).ConfigureAwait(true);
    }

    /// <summary>Removes the selected member game from its collection.</summary>
    /// <returns>A task that completes once the game has been un-filed.</returns>
    [RelayCommand]
    private async Task RemoveGameAsync()
    {
        if (SelectedMemberGame is not { } game)
        {
            return;
        }

        await MoveAsync(game.Id, null).ConfigureAwait(true);
    }

    /// <summary>Reloads membership when the selected collection changes.</summary>
    /// <param name="value">The newly selected collection.</param>
    partial void OnSelectedCollectionChanged(CollectionListItem? value)
    {
        RenameText = value?.Name ?? string.Empty;
        OnPropertyChanged(nameof(HasSelection));

        // Fire-and-forget is acceptable here only because LoadMembershipAsync
        // handles its own failures; nothing downstream awaits this.
        _ = LoadMembershipAsync(CancellationToken.None);
    }

    /// <summary>Moves a game into or out of a collection.</summary>
    /// <param name="gameId">The game to move.</param>
    /// <param name="collectionId">Target collection, or <see langword="null"/> to un-file it.</param>
    private async Task MoveAsync(int gameId, int? collectionId)
    {
        ClearError();

        try
        {
            await _games.AssignCollectionAsync([gameId], collectionId).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moving game {GameId} to collection {CollectionId} failed.", gameId, collectionId);
            SetErrorMessage("The game could not be moved.");
        }
    }

    /// <summary>Loads the games inside and outside the selected collection.</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    private async Task LoadMembershipAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SelectedCollection is not { } selected)
            {
                GamesInCollection = [];
                AvailableGames = [];
                return;
            }

            var all = await _games.GetAllAsync(cancellationToken).ConfigureAwait(true);

            GamesInCollection = new ObservableCollection<GameItemViewModel>(
                all.Where(game => game.CollectionId == selected.Id)
                   .Select(game => new GameItemViewModel(game, selected.Name)));

            // Games already in another collection are offered too: moving one here
            // is a legitimate action, and hiding them would make a game the user
            // can see in the library simply absent from this list.
            AvailableGames = new ObservableCollection<GameItemViewModel>(
                all.Where(game => game.CollectionId != selected.Id)
                   .Select(game => new GameItemViewModel(game)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading collection membership failed.");
            SetErrorMessage("The games in this collection could not be loaded.");
        }
    }
}
