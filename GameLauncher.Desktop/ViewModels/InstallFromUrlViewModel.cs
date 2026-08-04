using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Desktop.Services.Library;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the Install from URL dialog.
/// </summary>
/// <remarks>
/// Runs in two acts. The first downloads, verifies and unpacks; the second shows
/// what was found and waits for the user to choose which executable the library
/// entry should launch. Nothing is registered until they do.
/// </remarks>
public sealed partial class InstallFromUrlViewModel : DialogViewModelBase
{
    private readonly IInstallFromUrlService _install;
    private readonly IGameImportService _import;
    private readonly IIconExtractionService _icons;
    private readonly ILogger<InstallFromUrlViewModel> _logger;

    private CancellationTokenSource? _cts;
    private InstallPreparationResult? _preparation;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _expectedChecksum = string.Empty;

    [ObservableProperty]
    private string _statusText = "Paste a direct download link to begin.";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private bool _hasCandidates;

    [ObservableProperty]
    private ObservableCollection<DiscoveredGameItemViewModel> _candidates = [];

    [ObservableProperty]
    private DiscoveredGameItemViewModel? _selectedCandidate;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private string _installDirectory = string.Empty;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="install">Downloads, verifies and unpacks.</param>
    /// <param name="import">Registers the chosen executable.</param>
    /// <param name="icons">Produces icon previews for the candidate list.</param>
    /// <param name="logger">Logger for dialog diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public InstallFromUrlViewModel(
        IInstallFromUrlService install,
        IGameImportService import,
        IIconExtractionService icons,
        ILogger<InstallFromUrlViewModel> logger)
    {
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the game that was added, or <see langword="null"/> if none was.</summary>
    public Game? AddedGame { get; private set; }

    /// <summary>Gets a value indicating whether a download can be started.</summary>
    public bool CanStart => !IsWorking && !string.IsNullOrWhiteSpace(Url);

    /// <summary>Gets a value indicating whether the chosen executable can be registered.</summary>
    public bool CanRegister => !IsWorking && SelectedCandidate is not null;

    /// <summary>Downloads, verifies, unpacks and inspects the supplied link.</summary>
    /// <returns>A task that completes when the install has been prepared.</returns>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ClearError();
        Warning = null;
        HasCandidates = false;
        Candidates = [];

        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri))
        {
            SetErrorMessage("That is not a valid URL.");
            return;
        }

        IsWorking = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<InstallProgress>(update =>
            {
                StatusText = update.Message;

                // Switches to a determinate bar only once the stage can actually
                // report a fraction; a bar that sits at zero reads as stalled.
                IsProgressIndeterminate = update.Fraction is null;
                ProgressValue = update.Fraction ?? 0;
            });

            var result = await _install.PrepareAsync(
                new InstallFromUrlRequest
                {
                    Url = uri,
                    ExpectedChecksum = string.IsNullOrWhiteSpace(ExpectedChecksum) ? null : ExpectedChecksum.Trim()
                },
                progress,
                _cts.Token).ConfigureAwait(true);

            _preparation = result;
            InstallDirectory = result.InstallDirectory;
            Warning = result.Warning;

            var items = result.Candidates
                .Select(candidate => new DiscoveredGameItemViewModel(
                    candidate,
                    _icons.ExtractImage(candidate.ExecutablePath)))
                .ToList();

            Candidates = new ObservableCollection<DiscoveredGameItemViewModel>(items);
            HasCandidates = items.Count > 0;

            // Pre-selects the best guess so the common case is a single click,
            // while still leaving the decision visible and changeable.
            SelectedCandidate = items.FirstOrDefault(item => item.Discovered.IsLikelyGame) ?? items.FirstOrDefault();

            StatusText = items.Count switch
            {
                0 => "Download complete, but no executable was found.",
                1 => "Download complete. One executable found.",
                _ => $"Download complete. {items.Count} executables found — choose which one to launch."
            };
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled. A partial download was kept so it can be resumed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing from {Url} failed.", Url);
            SetErrorMessage(ex.Message);
            StatusText = "The install did not complete.";
        }
        finally
        {
            IsWorking = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Registers the selected executable in the library.</summary>
    /// <returns>A task that completes once the game has been added.</returns>
    [RelayCommand(CanExecute = nameof(CanRegister))]
    private async Task RegisterAsync()
    {
        if (SelectedCandidate is null || _preparation is null)
        {
            return;
        }

        IsWorking = true;
        ClearError();

        try
        {
            var result = await _import.ImportAsync(new GameImportRequest
            {
                ExecutablePath = SelectedCandidate.ExecutablePath,
                Title = SelectedCandidate.Title,
                InstallDirectory = _preparation.InstallDirectory,

                // Recorded so the details page can show where the game came from.
                SourceUrl = Url.Trim()
            }).ConfigureAwait(true);

            switch (result.Status)
            {
                case GameImportStatus.Added:
                    AddedGame = result.Game;
                    RequestClose(true);
                    break;

                case GameImportStatus.AlreadyInLibrary:
                    SetErrorMessage(result.Message ?? "That game is already in your library.");
                    break;

                default:
                    SetErrorMessage(result.Message ?? "The game could not be added.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registering the installed game failed.");
            SetErrorMessage($"The game could not be added: {ex.Message}");
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Cancels a download in progress.</summary>
    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();

    /// <summary>Closes the dialog.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        RequestClose(AddedGame is not null);
    }

    /// <summary>Refreshes command availability when the link changes.</summary>
    /// <param name="value">The new URL text.</param>
    partial void OnUrlChanged(string value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Refreshes command availability while work is running.</summary>
    /// <param name="value">Whether work is in progress.</param>
    partial void OnIsWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanRegister));
        StartCommand.NotifyCanExecuteChanged();
        RegisterCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Refreshes command availability when the chosen executable changes.</summary>
    /// <param name="value">The newly selected candidate.</param>
    partial void OnSelectedCandidateChanged(DiscoveredGameItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanRegister));
        RegisterCommand.NotifyCanExecuteChanged();
    }
}
