using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Helpers;
using GameLauncher.Desktop.Infrastructure.Navigation;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the Home landing page.
/// </summary>
/// <remarks>
/// Shows what the user is most likely to have opened the launcher for — the games
/// they were last playing — and a short summary of the library. Everything here
/// is read from repositories that already existed; the page owns no state and
/// changes nothing.
/// </remarks>
public sealed partial class HomeViewModel : ViewModelBase
{
    /// <summary>How many recently played games the page offers.</summary>
    /// <remarks>
    /// Six fits one row at the shell's minimum width. A landing page that scrolls
    /// is no longer a summary.
    /// </remarks>
    private const int RecentLimit = 6;

    private readonly IGameRepository _games;
    private readonly IAchievementRepository _achievements;
    private readonly INavigationService _navigation;
    private readonly ILogger<HomeViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<GameItemViewModel> _recentGames = [];

    [ObservableProperty]
    private bool _hasLibrary;

    [ObservableProperty]
    private bool _hasRecentGames;

    [ObservableProperty]
    private string _gamesOwnedText = "0";

    [ObservableProperty]
    private string _playtimeText = "Never played";

    [ObservableProperty]
    private string _achievementsText = "0";

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="games">Supplies recently played games and library totals.</param>
    /// <param name="achievements">Supplies the unlocked achievement count.</param>
    /// <param name="navigation">Used to open a game or the library.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public HomeViewModel(
        IGameRepository games,
        IAchievementRepository achievements,
        INavigationService navigation,
        ILogger<HomeViewModel> logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(cancellationToken);

    /// <summary>
    /// Loads the recently played games and the library summary.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A task that completes when the page is populated.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ClearError();

        try
        {
            var recent = await _games.GetRecentlyPlayedAsync(RecentLimit, cancellationToken).ConfigureAwait(true);
            var owned = await _games.CountAsync(cancellationToken).ConfigureAwait(true);
            var seconds = await _games.GetTotalPlaytimeSecondsAsync(cancellationToken).ConfigureAwait(true);
            var unlocked = await _achievements.GetUnlockCountAsync(cancellationToken).ConfigureAwait(true);

            RecentGames = new ObservableCollection<GameItemViewModel>(
                recent.Select(game => new GameItemViewModel(game)));

            HasRecentGames = RecentGames.Count > 0;
            HasLibrary = owned > 0;

            GamesOwnedText = owned.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            PlaytimeText = PlaytimeConverter.Format(seconds);
            AchievementsText = unlocked.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the home page failed.");
            SetErrorMessage("Your library summary could not be loaded.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens a game's details page.
    /// </summary>
    /// <param name="item">The game to open.</param>
    /// <returns>A task that completes when navigation finishes.</returns>
    /// <remarks>
    /// Opens the game rather than launching it. Launching from here would mean a
    /// second copy of the details page's error handling — missing executable,
    /// refused process start — living in a view model whose job is to summarise.
    /// </remarks>
    [RelayCommand]
    private async Task OpenGameAsync(GameItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _navigation.NavigateToAsync<GameDetailsViewModel, int>(item.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening game {GameId} from the home page failed.", item.Id);
            SetErrorMessage($"Could not open {item.Title}.");
        }
    }

    /// <summary>Opens the library.</summary>
    /// <returns>A task that completes when navigation finishes.</returns>
    [RelayCommand]
    private async Task BrowseLibraryAsync()
    {
        try
        {
            await _navigation.NavigateToAsync<LibraryViewModel>().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the library from the home page failed.");
            SetErrorMessage("Could not open your library.");
        }
    }
}
