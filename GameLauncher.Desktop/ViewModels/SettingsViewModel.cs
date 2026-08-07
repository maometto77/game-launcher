using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the Settings page.
/// </summary>
/// <remarks>
/// Edits a working copy and writes it only when the user saves, so abandoning
/// the page changes nothing. The friend code is shown but never editable: it is
/// this installation's identity, and letting somebody type over it would break
/// every existing friendship silently.
/// </remarks>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<string> _libraryFolders = [];

    [ObservableProperty]
    private string? _selectedFolder;

    [ObservableProperty]
    private bool _autoScanOnStartup;

    [ObservableProperty]
    private AppTheme _selectedTheme = AppTheme.Dark;

    [ObservableProperty]
    private string _relayUrl = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _friendCode = string.Empty;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isRegisteredWithRelay;

    [ObservableProperty]
    private bool _themeChangePending;

    [ObservableProperty]
    private string _steamGridDbApiKey = string.Empty;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="settings">Settings persistence.</param>
    /// <param name="dialogs">Folder picker and prompts.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SettingsViewModel(
        ISettingsService settings,
        IDialogService dialogs,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the themes offered by the picker.</summary>
    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    /// <inheritdoc />
    public override Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        var current = _settings.Current;

        LibraryFolders = new ObservableCollection<string>(current.LibraryFolders);
        AutoScanOnStartup = current.AutoScanOnStartup;
        SelectedTheme = current.Theme;
        RelayUrl = current.RelayUrl ?? string.Empty;
        DisplayName = current.DisplayName;
        // The relay's code when there is one, since that is what other people can
        // actually use; the local one otherwise.
        FriendCode = current.EffectiveFriendCode;
        IsRegisteredWithRelay = current.IsRegistered;
        SteamGridDbApiKey = current.SteamGridDbApiKey ?? string.Empty;

        ThemeChangePending = false;
        StatusText = null;
        ClearError();

        return Task.CompletedTask;
    }

    /// <summary>Adds a folder to the scan list.</summary>
    [RelayCommand]
    private void AddFolder()
    {
        var path = _dialogs.PickFolder("Select a folder that contains games");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (LibraryFolders.Any(folder => string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "That folder is already in the list.";
            return;
        }

        LibraryFolders.Add(path);
        StatusText = null;
    }

    /// <summary>Removes the selected folder from the scan list.</summary>
    [RelayCommand]
    private void RemoveFolder()
    {
        if (SelectedFolder is { } folder)
        {
            LibraryFolders.Remove(folder);
            SelectedFolder = null;
        }
    }

    /// <summary>Copies the friend code to the clipboard.</summary>
    [RelayCommand]
    private void CopyFriendCode()
    {
        try
        {
            System.Windows.Clipboard.SetText(FriendCode);
            StatusText = "Friend code copied.";
        }
        catch (Exception ex)
        {
            // The clipboard is a shared OS resource and another process can be
            // holding it; failing to copy is not worth an error banner.
            _logger.LogDebug(ex, "Copying the friend code to the clipboard failed.");
            StatusText = "Could not access the clipboard.";
        }
    }

    /// <summary>Validates and persists the settings.</summary>
    /// <returns>A task that completes when the settings have been written.</returns>
    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearError();

        var relay = RelayUrl.Trim();

        // Validated before saving rather than at connect time, so a typo is
        // reported next to the field that caused it.
        if (relay.Length > 0 &&
            (!Uri.TryCreate(relay, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            SetErrorMessage("The relay address must be a full http or https URL.");
            return;
        }

        var name = DisplayName.Trim();
        if (name.Length == 0)
        {
            SetErrorMessage("A display name is required; it is what friends see.");
            return;
        }

        try
        {
            var previous = _settings.Current;

            await _settings.SaveAsync(previous with
            {
                LibraryFolders = LibraryFolders.ToArray(),
                AutoScanOnStartup = AutoScanOnStartup,
                Theme = SelectedTheme,
                RelayUrl = relay.Length == 0 ? null : relay,
                DisplayName = name,
                SteamGridDbApiKey = SteamGridDbApiKey.Trim() is { Length: > 0 } artworkKey ? artworkKey : null
            }).ConfigureAwait(true);

            ThemeChangePending = SelectedTheme != previous.Theme;
            StatusText = ThemeChangePending
                ? "Saved. The new theme applies when GameLauncher restarts."
                : "Saved.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed.");
            SetErrorMessage($"Settings could not be saved: {ex.Message}");
        }
    }

    /// <summary>Restores the fields to the last saved values.</summary>
    /// <returns>A task that completes when the fields have been reset.</returns>
    [RelayCommand]
    private Task RevertAsync() => OnNavigatedToAsync();
}
