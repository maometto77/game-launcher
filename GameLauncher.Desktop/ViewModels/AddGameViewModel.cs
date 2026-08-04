using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Helpers;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Library;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the Add Game dialog: pick an executable, confirm the details
/// the launcher inferred, and add it.
/// </summary>
public sealed partial class AddGameViewModel : DialogViewModelBase
{
    private readonly IGameImportService _import;
    private readonly IExecutableInspector _inspector;
    private readonly IIconExtractionService _icons;
    private readonly ICollectionRepository _collectionRepository;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AddGameViewModel> _logger;

    [ObservableProperty]
    private string _executablePath = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _installDirectory = string.Empty;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Collection> _collections = [];

    [ObservableProperty]
    private Collection? _selectedCollection;

    [ObservableProperty]
    private ImageSource? _iconPreview;

    [ObservableProperty]
    private string _detectedSummary = string.Empty;

    [ObservableProperty]
    private string? _validationWarning;

    [ObservableProperty]
    private bool _hasExecutable;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="import">Performs the import.</param>
    /// <param name="inspector">Reads metadata from the chosen executable.</param>
    /// <param name="icons">Produces the icon preview.</param>
    /// <param name="collections">Supplies the collection list.</param>
    /// <param name="dialogs">File picker and error prompts.</param>
    /// <param name="logger">Logger for dialog diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AddGameViewModel(
        IGameImportService import,
        IExecutableInspector inspector,
        IIconExtractionService icons,
        ICollectionRepository collections,
        IDialogService dialogs,
        ILogger<AddGameViewModel> logger)
    {
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _collectionRepository = collections ?? throw new ArgumentNullException(nameof(collections));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the game that was added, or <see langword="null"/> if the dialog was cancelled.</summary>
    public Game? AddedGame { get; private set; }

    /// <summary>Gets a value indicating whether the dialog has enough information to add a game.</summary>
    public bool CanAdd => HasExecutable && !string.IsNullOrWhiteSpace(Title) && !IsBusy;

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var collections = await _collectionRepository.GetAllAsync(cancellationToken).ConfigureAwait(true);
            Collections = new ObservableCollection<Collection>(collections);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading collections for the Add Game dialog failed.");
        }
    }

    /// <summary>
    /// Asks the user for an executable and populates the form from it.
    /// </summary>
    /// <returns>A task that completes when the form has been filled in.</returns>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = _dialogs.PickFile(
            "Select the game's executable",
            "Executables (*.exe)|*.exe|All files (*.*)|*.*");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await LoadExecutableAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads an executable and fills the form with what it reports.
    /// </summary>
    /// <param name="path">Absolute path to the executable.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the form reflects the executable.</returns>
    public async Task LoadExecutableAsync(string path, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ClearError();
        ValidationWarning = null;

        try
        {
            var info = await _inspector.InspectAsync(path, cancellationToken).ConfigureAwait(true);

            ExecutablePath = info.Path;
            Title = info.SuggestedTitle;
            InstallDirectory = GameScanService.ResolveInstallDirectory(info.Path);
            IconPreview = _icons.ExtractImage(info.Path);
            HasExecutable = true;

            DetectedSummary = string.Join("  ·  ", new[]
                {
                    info.PlatformSummary,
                    ByteSizeConverter.Format(info.FileSizeBytes),
                    info.CompanyName
                }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

            var validation = await _inspector.ValidateAsync(info.Path, cancellationToken).ConfigureAwait(true);
            if (validation.Problem is { } problem)
            {
                // Surfaced but not blocking: the user may genuinely want to add a
                // launcher or a tool, and the heuristic is only a guess.
                ValidationWarning = problem;
            }

            OnPropertyChanged(nameof(CanAdd));
            AddCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading {Path} failed.", path);
            SetErrorMessage($"That file could not be read: {ex.Message}");
            HasExecutable = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Imports the game and closes the dialog.</summary>
    /// <returns>A task that completes once the import has finished.</returns>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        IsBusy = true;
        ClearError();

        try
        {
            var request = new GameImportRequest
            {
                ExecutablePath = ExecutablePath,
                Title = Title,
                InstallDirectory = InstallDirectory,
                Tags = ParseTags(TagsText),
                CollectionId = SelectedCollection?.Id
            };

            var result = await _import.ImportAsync(request).ConfigureAwait(true);

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
            _logger.LogError(ex, "Adding {Path} failed.", ExecutablePath);
            SetErrorMessage($"The game could not be added: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Closes the dialog without adding anything.</summary>
    [RelayCommand]
    private void Cancel() => RequestClose(false);

    /// <summary>Lets the user override the inferred install folder.</summary>
    [RelayCommand]
    private void BrowseInstallDirectory()
    {
        var path = _dialogs.PickFolder(
            "Select the game's install folder",
            string.IsNullOrWhiteSpace(InstallDirectory) ? null : InstallDirectory);

        if (!string.IsNullOrWhiteSpace(path))
        {
            InstallDirectory = path;
        }
    }

    /// <summary>Re-evaluates whether the game can be added when the title changes.</summary>
    /// <param name="value">The new title.</param>
    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(CanAdd));
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Splits a comma-separated tag entry into individual tags.
    /// </summary>
    /// <param name="text">Raw text as typed.</param>
    /// <returns>Trimmed, de-duplicated, non-empty tags.</returns>
    internal static IReadOnlyList<string> ParseTags(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
}
