using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Helpers;
using GameLauncher.Desktop.Infrastructure.Navigation;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Launcher;
using GameLauncher.Desktop.Services.Library;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for a single game's details page.
/// </summary>
public sealed partial class GameDetailsViewModel : ViewModelBase, INavigationTarget<int>, IDisposable
{
    private readonly IGameRepository _games;
    private readonly IAchievementRepository _achievements;
    private readonly IPlaySessionRepository _sessions;
    private readonly ILibraryService _library;
    private readonly IGameLaunchService _launcher;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<GameDetailsViewModel> _logger;

    private int _gameId;
    private bool _disposed;

    [ObservableProperty]
    private Game? _game;

    [ObservableProperty]
    private ObservableCollection<AchievementItemViewModel> _achievementItems = [];

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isExecutableMissing;

    [ObservableProperty]
    private string _playtimeText = string.Empty;

    [ObservableProperty]
    private string _installSizeText = string.Empty;

    [ObservableProperty]
    private string _lastPlayedText = string.Empty;

    [ObservableProperty]
    private string _sessionCountText = string.Empty;

    [ObservableProperty]
    private string _achievementSummary = string.Empty;

    [ObservableProperty]
    private bool _hasUnsavedNotes;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="games">Game persistence.</param>
    /// <param name="achievements">Achievement persistence.</param>
    /// <param name="sessions">Play session persistence.</param>
    /// <param name="library">Library application logic.</param>
    /// <param name="launcher">Launch and playtime tracking.</param>
    /// <param name="dialogs">Confirmation prompts.</param>
    /// <param name="navigation">Used to leave the page after uninstalling.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GameDetailsViewModel(
        IGameRepository games,
        IAchievementRepository achievements,
        IPlaySessionRepository sessions,
        ILibraryService library,
        IGameLaunchService launcher,
        IDialogService dialogs,
        INavigationService navigation,
        ILogger<GameDetailsViewModel> logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _launcher.GameStarted += OnGameStateChanged;
        _launcher.GameExited += OnGameStateChanged;
    }

    /// <summary>Gets a value indicating whether the game can currently be launched.</summary>
    public bool CanPlay => Game is not null && !IsRunning && !IsExecutableMissing;

    /// <inheritdoc />
    public Task InitializeAsync(int parameter, CancellationToken cancellationToken = default)
    {
        _gameId = parameter;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(cancellationToken);

    /// <inheritdoc />
    public override async Task OnNavigatedFromAsync()
    {
        // Notes are edited in place with no explicit Save gesture in the common
        // case, so leaving the page commits them rather than silently discarding.
        if (HasUnsavedNotes)
        {
            await PersistNotesAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Loads the game, its achievements and its session statistics.
    /// </summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A task that completes when the page is populated.</returns>
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ClearError();

        try
        {
            var game = await _games.GetByIdAsync(_gameId, cancellationToken).ConfigureAwait(true);
            if (game is null)
            {
                SetErrorMessage("This game is no longer in your library.");
                return;
            }

            Game = game;
            Notes = game.Notes ?? string.Empty;
            HasUnsavedNotes = false;

            IsRunning = _launcher.IsRunning(game.Id);
            IsExecutableMissing = !game.ExecutableExists;

            PlaytimeText = PlaytimeConverter.Format(game.PlaytimeSeconds);
            InstallSizeText = ByteSizeConverter.Format(game.InstallSizeBytes);
            LastPlayedText = game.LastPlayedAt is { } played
                ? RelativeTimeConverter.Format(played)
                : "Never played";

            var sessionCount = await _sessions.CountForGameAsync(game.Id, cancellationToken).ConfigureAwait(true);
            SessionCountText = sessionCount == 1 ? "1 session" : $"{sessionCount} sessions";

            await LoadAchievementsAsync(game.CatalogId, cancellationToken).ConfigureAwait(true);

            OnPropertyChanged(nameof(CanPlay));
            PlayCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading game {GameId} failed.", _gameId);
            SetErrorMessage("This game's details could not be loaded.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Loads the achievement list, marking which are unlocked.</summary>
    /// <param name="catalogId">
    /// Shared catalog identity of the title, or <see langword="null"/> for a game
    /// that has not been assigned one.
    /// </param>
    /// <param name="cancellationToken">Cancels the load.</param>
    private async Task LoadAchievementsAsync(string? catalogId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            AchievementItems = [];
            AchievementSummary = "No achievements configured";
            return;
        }

        var definitions = await _achievements
            .GetDefinitionsForCatalogAsync(catalogId, cancellationToken)
            .ConfigureAwait(true);

        var unlocks = await _achievements.GetUnlocksAsync(cancellationToken).ConfigureAwait(true);
        var unlockTimes = unlocks.ToDictionary(unlock => unlock.DefinitionId, unlock => unlock.UnlockedAt);

        var items = definitions
            .Select(definition => new AchievementItemViewModel(
                definition,
                unlockTimes.TryGetValue(definition.Id, out var stamp) ? stamp : null))

            // Unlocked first, then alphabetical, so earned achievements are what
            // the user sees without scrolling.
            .OrderByDescending(item => item.IsUnlocked)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        AchievementItems = new ObservableCollection<AchievementItemViewModel>(items);

        var unlocked = items.Count(item => item.IsUnlocked);
        AchievementSummary = items.Count == 0
            ? "No achievements configured"
            : $"{unlocked} of {items.Count} unlocked";
    }

    /// <summary>Launches the game.</summary>
    /// <returns>A task that completes once the process has been started.</returns>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (Game is null)
        {
            return;
        }

        ClearError();

        try
        {
            await _launcher.LaunchAsync(Game).ConfigureAwait(true);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Executable missing for {Title}.", Game.Title);
            IsExecutableMissing = true;
            SetErrorMessage($"The executable for {Game.Title} could not be found at {Game.ExecutablePath}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launching {Title} failed.", Game.Title);
            SetErrorMessage($"{Game.Title} could not be started: {ex.Message}");
        }
    }

    /// <summary>Saves the notes field.</summary>
    /// <returns>A task that completes when the notes have been stored.</returns>
    [RelayCommand]
    private Task SaveNotesAsync() => PersistNotesAsync(CancellationToken.None);

    /// <summary>
    /// Removes the game from the library, offering to delete its files.
    /// </summary>
    /// <returns>A task that completes once the game has been removed.</returns>
    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (Game is null)
        {
            return;
        }

        var title = Game.Title;

        if (!_dialogs.Confirm(
                "Remove game",
                $"Remove {title} from your library?",
                isDestructive: true))
        {
            return;
        }

        // Asked as a separate question so that removing the entry and erasing
        // files from disk are never the same click.
        var deleteFiles = !string.IsNullOrWhiteSpace(Game.InstallDir)
                          && Directory.Exists(Game.InstallDir)
                          && _dialogs.Confirm(
                              "Delete files",
                              $"Also delete the installed files in:\n{Game.InstallDir}\n\n" +
                              "This cannot be undone.",
                              isDestructive: true);

        try
        {
            var result = await _library.UninstallAsync(Game.Id, deleteFiles).ConfigureAwait(true);

            if (result.FileDeletionError is { } error)
            {
                _dialogs.ShowError("Files not deleted", $"{title} was removed from your library, but {error}");
            }

            await _navigation.NavigateToAsync<LibraryViewModel>().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Uninstalling {Title} failed.", title);
            SetErrorMessage($"{title} could not be removed: {ex.Message}");
        }
    }

    /// <summary>Tracks that the notes field has been edited.</summary>
    /// <param name="value">The new notes text.</param>
    partial void OnNotesChanged(string value)
    {
        if (Game is not null)
        {
            HasUnsavedNotes = !string.Equals(value, Game.Notes ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>Writes the notes field to storage.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task PersistNotesAsync(CancellationToken cancellationToken)
    {
        if (Game is null || !HasUnsavedNotes)
        {
            return;
        }

        try
        {
            await _library.SaveNotesAsync(Game.Id, Notes, cancellationToken).ConfigureAwait(true);
            Game.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
            HasUnsavedNotes = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving notes for game {GameId} failed.", Game.Id);
            SetErrorMessage("Your notes could not be saved.");
        }
    }

    /// <summary>Refreshes running state when this game starts or stops.</summary>
    /// <param name="sender">The launch service.</param>
    /// <param name="e">Details of the session that changed.</param>
    private void OnGameStateChanged(object? sender, GameSessionEventArgs e)
    {
        if (Game is null || e.Game.Id != Game.Id)
        {
            return;
        }

        IsRunning = _launcher.IsRunning(Game.Id);
        PlaytimeText = PlaytimeConverter.Format(Game.PlaytimeSeconds);
        LastPlayedText = Game.LastPlayedAt is { } played
            ? RelativeTimeConverter.Format(played)
            : "Never played";

        OnPropertyChanged(nameof(CanPlay));
        PlayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Keeps the Play button in step with running state.</summary>
    /// <param name="value">Whether the game is running.</param>
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        PlayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Detaches from launch events.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _launcher.GameStarted -= OnGameStateChanged;
        _launcher.GameExited -= OnGameStateChanged;
    }
}
