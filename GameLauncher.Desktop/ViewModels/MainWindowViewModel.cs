using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Infrastructure.Navigation;
using GameLauncher.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the application shell: sidebar, header and the hosted page.
/// </summary>
/// <remarks>
/// Owns no domain state of its own. It reflects the navigation service into
/// bindable properties and turns sidebar input into navigation calls.
/// </remarks>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly ILogger<MainWindowViewModel> _logger;
    private bool _disposed;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private NavigationSection _activeSection = NavigationSection.Home;

    [ObservableProperty]
    private bool _canGoBack;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="navigation">Navigation service driving the hosted page.</param>
    /// <param name="toasts">Achievement toast overlay, hosted above the current page.</param>
    /// <param name="logger">Logger for shell-level diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MainWindowViewModel(
        INavigationService navigation,
        AchievementToastHostViewModel toasts,
        ILogger<MainWindowViewModel> logger)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _navigation.CurrentChanged += OnNavigationCurrentChanged;
    }

    /// <summary>
    /// Gets the achievement toast overlay.
    /// </summary>
    /// <remarks>
    /// Owned by the shell rather than by a page, because an achievement can be
    /// earned while the user is looking at any of them — or at none, with a game
    /// in the foreground.
    /// </remarks>
    public AchievementToastHostViewModel Toasts { get; }

    /// <summary>
    /// Performs the initial navigation once the window is shown.
    /// </summary>
    /// <param name="cancellationToken">Cancels the initial load.</param>
    /// <returns>A task that completes when the landing page is loaded.</returns>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(NavigationSection.Home, cancellationToken);

    /// <summary>
    /// Navigates to a top-level section.
    /// </summary>
    /// <param name="section">The section to show.</param>
    /// <returns>A task that completes when the section has loaded.</returns>
    [RelayCommand]
    private Task NavigateAsync(NavigationSection section) => NavigateAsync(section, CancellationToken.None);

    /// <summary>
    /// Returns to the previously visited page.
    /// </summary>
    /// <returns>A task that completes when the previous page is active.</returns>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        try
        {
            await _navigation.GoBackAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigating back failed.");
            SetErrorMessage("Could not go back to the previous page.");
        }
    }

    /// <summary>
    /// Navigates to <paramref name="section"/>, mapping it to the owning view
    /// model type.
    /// </summary>
    /// <param name="section">Section to display.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    private async Task NavigateAsync(NavigationSection section, CancellationToken cancellationToken)
    {
        if (ActiveSection == section && CurrentPage is not null)
        {
            return;
        }

        ClearError();

        // Switching top-level section resets the back stack: returning to a game
        // page the user left three sections ago is not what Back means to them.
        _navigation.ClearHistory();

        try
        {
            switch (section)
            {
                case NavigationSection.Home:
                    await _navigation.NavigateToAsync<HomeViewModel>(cancellationToken).ConfigureAwait(true);
                    break;

                case NavigationSection.Library:
                    await _navigation.NavigateToAsync<LibraryViewModel>(cancellationToken).ConfigureAwait(true);
                    break;

                case NavigationSection.Friends:
                    await _navigation.NavigateToAsync<FriendsViewModel>(cancellationToken).ConfigureAwait(true);
                    break;

                case NavigationSection.Collections:
                    await _navigation.NavigateToAsync<CollectionsViewModel>(cancellationToken).ConfigureAwait(true);
                    break;

                case NavigationSection.Achievements:
                    await _navigation.NavigateToAsync<AchievementsViewModel>(cancellationToken).ConfigureAwait(true);
                    break;

                case NavigationSection.Settings:
                    await _navigation.NavigateToAsync<SettingsViewModel>(cancellationToken).ConfigureAwait(true);
                    break;

                default:
                    // Unreachable: the sidebar only offers sections that are mapped.
                    throw new ArgumentOutOfRangeException(
                        nameof(section), section, "No view model is mapped to this navigation section.");
            }

            ActiveSection = section;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigating to {Section} failed.", section);
            SetErrorMessage($"Could not open {section}.");
        }
    }

    /// <summary>Mirrors navigation state onto bindable properties.</summary>
    private void OnNavigationCurrentChanged(object? sender, ViewModelBase? page)
    {
        CurrentPage = page;
        CanGoBack = _navigation.CanGoBack;
        GoBackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Detaches from the navigation service.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigation.CurrentChanged -= OnNavigationCurrentChanged;
    }
}
