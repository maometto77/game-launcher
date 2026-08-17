using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Infrastructure;
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
    private readonly IStartupNotices _notices;
    private readonly ILogger<MainWindowViewModel> _logger;

    /// <summary>The sub-view last open in each section, so returning restores it.</summary>
    private readonly Dictionary<NavigationSection, string> _lastSubSection = [];

    /// <summary>Set while a section change is assigning the selection itself.</summary>
    private bool _suppressSubNavigation;

    private bool _disposed;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private NavigationSection _activeSection = NavigationSection.Library;

    [ObservableProperty]
    private ObservableCollection<SubNavigationItem> _subSections = [];

    [ObservableProperty]
    private SubNavigationItem? _selectedSubSection;

    [ObservableProperty]
    private bool _hasSubSections;

    [ObservableProperty]
    private bool _canGoBack;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="navigation">Navigation service driving the hosted page.</param>
    /// <param name="toasts">Achievement toast overlay, hosted above the current page.</param>
    /// <param name="notices">Messages raised during startup, shown once a window exists.</param>
    /// <param name="logger">Logger for shell-level diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MainWindowViewModel(
        INavigationService navigation,
        AchievementToastHostViewModel toasts,
        IStartupNotices notices,
        ILogger<MainWindowViewModel> logger)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
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
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await NavigateAsync(NavigationSection.Library, cancellationToken).ConfigureAwait(true);

        // Shown after the first page rather than before it, so the banner appears
        // over a working window instead of an empty shell. Startup produces
        // notices only for things the user genuinely needs to know — a database
        // that had to be rebuilt, and nothing routine.
        if (_notices.Messages is { Count: > 0 } messages)
        {
            SetErrorMessage(string.Join(" ", messages));
            _notices.Clear();
        }
    }

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
    /// Shows a top-level section, reopening whichever sub-view was last active
    /// inside it.
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

        var tabs = BuildSubSections(section);

        // Remembered per section, so leaving Library on Achievements and coming
        // back does not silently drop the user somewhere else.
        var remembered = _lastSubSection.GetValueOrDefault(section);

        var target = tabs.FirstOrDefault(tab => string.Equals(tab.Key, remembered, StringComparison.Ordinal))
                     ?? tabs.FirstOrDefault();

        try
        {
            if (target is null)
            {
                // Unreachable: the sidebar only offers sections that are mapped.
                throw new ArgumentOutOfRangeException(
                    nameof(section), section, "No view model is mapped to this navigation section.");
            }

            await target.ActivateAsync(cancellationToken).ConfigureAwait(true);

            ActiveSection = section;
            SubSections = new ObservableCollection<SubNavigationItem>(tabs);

            // Assigned after the collection, so the selector is choosing from the
            // list it is about to display rather than the outgoing section's, and
            // suppressed so the assignment does not navigate a second time.
            _suppressSubNavigation = true;
            SelectedSubSection = target;
            _suppressSubNavigation = false;

            HasSubSections = tabs.Count > 1;
            _lastSubSection[section] = target.Key;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigating to {Section} failed.", section);
            SetErrorMessage($"Could not open {section}.");
        }
    }

    /// <summary>
    /// Lists the sub-views a section offers, in the order they appear.
    /// </summary>
    /// <param name="section">The section being shown.</param>
    /// <returns>Its sub-views; empty for a section with nothing mapped.</returns>
    /// <remarks>
    /// Every page is kept alive, so returning to one finds its filters, search
    /// text and scroll position as they were left. Sections with a single entry
    /// still return it — the view hides the strip rather than drawing one tab.
    /// </remarks>
    private IReadOnlyList<SubNavigationItem> BuildSubSections(NavigationSection section) => section switch
    {
        NavigationSection.Library =>
        [
            Tab("overview", "Overview", token => _navigation.NavigateToKeptAliveAsync<HomeViewModel>(token)),
            Tab("games", "Installed games", token => _navigation.NavigateToKeptAliveAsync<LibraryViewModel>(token)),
            Tab("collections", "Collections",
                token => _navigation.NavigateToKeptAliveAsync<CollectionsViewModel>(token)),
            Tab("achievements", "Achievements",
                token => _navigation.NavigateToKeptAliveAsync<AchievementsViewModel>(token))
        ],

        // One entry today. A second catalogue browser would sit beside Discover
        // here rather than claim its own sidebar row.
        NavigationSection.Search =>
        [
            Tab("discover", "Discover", token => _navigation.NavigateToKeptAliveAsync<DiscoverViewModel>(token))
        ],

        NavigationSection.Downloads =>
        [
            Tab("queue", "Queue", token => _navigation.NavigateToKeptAliveAsync<DownloadsViewModel>(token))
        ],

        NavigationSection.Friends =>
        [
            Tab("friends", "Friends", token => _navigation.NavigateToKeptAliveAsync<FriendsViewModel>(token))
        ],

        NavigationSection.Settings =>
        [
            Tab("settings", "Settings", token => _navigation.NavigateToKeptAliveAsync<SettingsViewModel>(token))
        ],

        _ => []
    };

    /// <summary>Builds one sub-navigation entry.</summary>
    /// <param name="key">Stable identifier, used to restore the last choice.</param>
    /// <param name="label">What the tab says.</param>
    /// <param name="activate">Shows the sub-view.</param>
    /// <returns>The entry.</returns>
    private static SubNavigationItem Tab(string key, string label, Func<CancellationToken, Task> activate) =>
        new() { Key = key, Label = label, ActivateAsync = activate };

    /// <summary>
    /// Shows the chosen sub-view when the user picks a tab.
    /// </summary>
    /// <param name="value">The newly selected tab.</param>
    /// <remarks>
    /// Routed through a command rather than started here, because a
    /// property-changed handler cannot be asynchronous. The command owns the
    /// in-flight task, so the navigation is observable instead of forgotten.
    /// </remarks>
    partial void OnSelectedSubSectionChanged(SubNavigationItem? value)
    {
        if (_suppressSubNavigation || value is null)
        {
            return;
        }

        _lastSubSection[ActiveSection] = value.Key;

        SelectSubSectionCommand.Execute(value);
    }

    /// <summary>Opens a sub-view, reporting failure rather than throwing.</summary>
    /// <param name="item">The tab to activate.</param>
    /// <returns>A task that completes once the sub-view has loaded, or failed visibly.</returns>
    /// <remarks>
    /// Nothing awaits this on the user's path through the interface, so every
    /// failure has to be caught here or it would surface as an unhandled
    /// exception on the dispatcher.
    /// </remarks>
    [RelayCommand]
    private async Task SelectSubSectionAsync(SubNavigationItem item)
    {
        ClearError();

        // As with a section change: a drill-down reached from the tab being left
        // is not somewhere Back should return to once the user has moved on.
        _navigation.ClearHistory();

        try
        {
            await item.ActivateAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening {Tab} failed.", item.Label);
            SetErrorMessage($"Could not open {item.Label}.");
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
