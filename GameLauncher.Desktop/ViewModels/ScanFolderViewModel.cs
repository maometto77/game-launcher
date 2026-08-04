using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Library;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the Scan Folder dialog: search a folder tree, review what was
/// found, and add the selections.
/// </summary>
/// <remarks>
/// Nothing discovered is ever added on its own. The scan produces a reviewable
/// list, likely games start ticked as a convenience, and the import only runs
/// when the user presses Add.
/// </remarks>
public sealed partial class ScanFolderViewModel : DialogViewModelBase
{
    private readonly IGameScanService _scanner;
    private readonly IGameImportService _import;
    private readonly IIconExtractionService _icons;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ScanFolderViewModel> _logger;

    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<DiscoveredGameItemViewModel> _results = [];

    [ObservableProperty]
    private string _statusText = "Choose a folder to search for games.";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasScanned;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="scanner">Performs the folder walk.</param>
    /// <param name="import">Imports the selected candidates.</param>
    /// <param name="icons">Produces icon previews for the results list.</param>
    /// <param name="dialogs">Folder picker and prompts.</param>
    /// <param name="logger">Logger for dialog diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ScanFolderViewModel(
        IGameScanService scanner,
        IGameImportService import,
        IIconExtractionService icons,
        IDialogService dialogs,
        ILogger<ScanFolderViewModel> logger)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the number of games actually added when the dialog was accepted.</summary>
    public int AddedCount { get; private set; }

    /// <summary>Gets a value indicating whether a scan can start.</summary>
    public bool CanScan => !IsScanning && !string.IsNullOrWhiteSpace(FolderPath);

    /// <summary>Gets a value indicating whether the selected candidates can be added.</summary>
    public bool CanAdd => !IsScanning && SelectedCount > 0;

    /// <summary>Asks the user for a folder to scan.</summary>
    [RelayCommand]
    private void BrowseFolder()
    {
        var path = _dialogs.PickFolder("Select a folder to search for games");
        if (!string.IsNullOrWhiteSpace(path))
        {
            FolderPath = path;
        }
    }

    /// <summary>Searches the chosen folder.</summary>
    /// <returns>A task that completes when the scan has finished or been cancelled.</returns>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        ClearError();
        IsScanning = true;
        Results = [];
        SelectedCount = 0;

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        try
        {
            var progress = new Progress<ScanProgress>(update =>
                StatusText = $"Searching… {update.DirectoriesScanned} folders, {update.CandidatesFound} executables found");

            var discovered = await _scanner
                .ScanAsync(FolderPath, ScanOptions.Default, progress, token)
                .ConfigureAwait(true);

            StatusText = "Reading icons…";

            // Icon extraction touches every candidate, so it runs off the UI
            // thread. Each image is frozen by the extractor, which is what makes
            // handing them back across the thread boundary legal.
            var items = await Task.Run(
                () => discovered
                    .Select(candidate => new DiscoveredGameItemViewModel(
                        candidate,
                        _icons.ExtractImage(candidate.ExecutablePath)))
                    .ToList(),
                token).ConfigureAwait(true);

            foreach (var item in items)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }

            Results = new ObservableCollection<DiscoveredGameItemViewModel>(items);
            HasScanned = true;
            RecountSelection();

            var likely = items.Count(item => item.Discovered.IsLikelyGame);
            StatusText = items.Count == 0
                ? "No executables were found in that folder."
                : $"Found {items.Count} executables; {likely} look like games.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (DirectoryNotFoundException ex)
        {
            SetErrorMessage(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scanning {Folder} failed.", FolderPath);
            SetErrorMessage($"That folder could not be scanned: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            ScanCommand.NotifyCanExecuteChanged();
            AddSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Stops a scan in progress.</summary>
    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>Ticks every selectable candidate.</summary>
    [RelayCommand]
    private void SelectAll() => SetSelection(item => item.IsSelectable);

    /// <summary>Clears every tick.</summary>
    [RelayCommand]
    private void SelectNone() => SetSelection(_ => false);

    /// <summary>Ticks only the candidates that look like games.</summary>
    [RelayCommand]
    private void SelectLikely() =>
        SetSelection(item => item.IsSelectable && item.Discovered.IsLikelyGame);

    /// <summary>Imports every ticked candidate.</summary>
    /// <returns>A task that completes when the import has finished.</returns>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddSelectedAsync()
    {
        var selected = Results.Where(item => item.IsSelected && item.IsSelectable).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        IsBusy = true;
        ClearError();

        try
        {
            var requests = selected
                .Select(item => new GameImportRequest
                {
                    ExecutablePath = item.ExecutablePath,
                    Title = item.Title,
                    InstallDirectory = item.Discovered.InstallDirectory,

                    // Skipped for bulk imports: walking dozens of install folders
                    // would stall the dialog. Sizes are filled in when a game's
                    // details page is opened.
                    MeasureInstallSize = false
                })
                .ToList();

            var completed = 0;
            var progress = new Progress<GameImportResult>(_ =>
                StatusText = $"Adding… {++completed} of {requests.Count}");

            var results = await _import.ImportManyAsync(requests, progress).ConfigureAwait(true);

            AddedCount = results.Count(result => result.Status == GameImportStatus.Added);
            var failed = results.Where(result => result.Status == GameImportStatus.Failed).ToList();

            if (failed.Count > 0)
            {
                _dialogs.ShowError(
                    "Some games were not added",
                    $"{AddedCount} added, {failed.Count} failed.\n\n" +
                    string.Join('\n', failed.Take(5).Select(result => result.Message)));
            }

            RequestClose(AddedCount > 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Importing scanned games failed.");
            SetErrorMessage($"The selected games could not be added: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Closes the dialog without importing.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _scanCts?.Cancel();
        RequestClose(false);
    }

    /// <inheritdoc />
    public override Task OnNavigatedFromAsync()
    {
        Detach();
        return Task.CompletedTask;
    }

    /// <summary>Applies a selection predicate to every result.</summary>
    /// <param name="predicate">Decides whether each item should be ticked.</param>
    private void SetSelection(Func<DiscoveredGameItemViewModel, bool> predicate)
    {
        foreach (var item in Results)
        {
            item.IsSelected = predicate(item);
        }

        RecountSelection();
    }

    /// <summary>Keeps the selected count in step with the ticks.</summary>
    /// <param name="sender">The row whose property changed.</param>
    /// <param name="e">Details of the change.</param>
    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoveredGameItemViewModel.IsSelected))
        {
            RecountSelection();
        }
    }

    /// <summary>Recalculates how many candidates are ticked.</summary>
    private void RecountSelection()
    {
        SelectedCount = Results.Count(item => item.IsSelected && item.IsSelectable);
        AddSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Unsubscribes from result rows and releases the scan token source.</summary>
    private void Detach()
    {
        foreach (var item in Results)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        _scanCts?.Dispose();
        _scanCts = null;
    }

    /// <summary>Refreshes command availability when the folder changes.</summary>
    /// <param name="value">The newly chosen folder.</param>
    partial void OnFolderPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanScan));
        ScanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Refreshes command availability while scanning.</summary>
    /// <param name="value">Whether a scan is running.</param>
    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanAdd));
    }

    /// <summary>Refreshes command availability when the tick count changes.</summary>
    /// <param name="value">The new selected count.</param>
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(CanAdd));
}
