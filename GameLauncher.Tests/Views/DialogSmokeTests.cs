using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Friends;
using GameLauncher.Desktop.Services.Notifications;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Desktop.ViewModels;
using GameLauncher.Desktop.Views;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Views;

/// <summary>
/// Verifies that every window and page can actually be built and laid out.
/// </summary>
/// <remarks>
/// <para>
/// A missing <c>StaticResource</c>, a style whose <c>TargetType</c> does not
/// match, or a <c>BasedOn</c> pointing at a key that no longer exists all
/// compile without complaint and throw only when the XAML is parsed or a
/// template is expanded. A window that is never opened is therefore never really
/// tested by a build.
/// </para>
/// <para>
/// Populating the list-bearing view models before layout is essential rather
/// than incidental. An <c>ItemsControl</c> with no items never instantiates its
/// <c>DataTemplate</c>, so a broken resource inside that template stays
/// invisible — these tests were confirmed against a deliberately broken key to
/// make sure they actually fail.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class DialogSmokeTests
{
    private readonly WpfTestHost _wpf;

    public DialogSmokeTests(WpfTestHost wpf) => _wpf = wpf;

    [Fact]
    public async Task MainWindow_realises_every_section_and_its_sub_navigation()
    {
        using var host = new TestAppHost();
        host.SeedSampleData();

        var shell = host.Resolve<MainWindowViewModel>();

        await _wpf.InvokeAsync(async () =>
        {
            var window = host.Resolve<MainWindow>();
            PositionOffScreen(window);

            try
            {
                window.Show();

                // Every section in turn, with a layout pass after each. The
                // sub-navigation strip has no tabs until something navigates, and
                // an empty ItemsControl never instantiates its item container —
                // so a shell realised before the first navigation would prove
                // nothing about the strip at all.
                foreach (var section in Enum.GetValues<NavigationSection>())
                {
                    await shell.NavigateCommand.ExecuteAsync(section);
                    window.UpdateLayout();
                }
            }
            finally
            {
                window.Close();
            }
        });

        Assert.False(shell.HasError, shell.ErrorMessage);
    }

    [Fact]
    public void AddGameWindow_builds_and_lays_out()
    {
        using var host = new TestAppHost();
        _wpf.Invoke(() => Realise(host.Resolve<AddGameWindow>()));
    }

    [Fact]
    public void ScanFolderWindow_realises_its_result_rows()
    {
        using var host = new TestAppHost();

        _wpf.Invoke(() =>
        {
            var window = host.Resolve<ScanFolderWindow>();
            var viewModel = (ScanFolderViewModel)window.DataContext;

            // Set before layout so the results list has rows to template.
            viewModel.Results = new ObservableCollection<DiscoveredGameItemViewModel>
            {
                new(SampleDiscovery(), icon: null)
            };

            Realise(window);
        });
    }

    [Fact]
    public void InstallFromUrlWindow_builds_and_lays_out()
    {
        using var host = new TestAppHost();
        _wpf.Invoke(() => Realise(host.Resolve<InstallFromUrlWindow>()));
    }

    [Fact]
    public async Task LibraryPage_realises_its_game_tiles()
    {
        using var host = new TestAppHost();
        host.SeedSampleData();

        // Awaited here rather than inside _wpf.Invoke on purpose. The view models
        // await with ConfigureAwait(true); blocking the dispatcher thread on them
        // would deadlock against their own continuations.
        var viewModel = host.Resolve<LibraryViewModel>();
        await viewModel.LoadAsync();

        Assert.NotEmpty(viewModel.VisibleGames);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task LibraryPage_realises_in_list_mode()
    {
        using var host = new TestAppHost();
        host.SeedSampleData();

        var viewModel = host.Resolve<LibraryViewModel>();
        await viewModel.LoadAsync();

        // The grid and list presentations use different item templates, so each
        // needs realising to be covered.
        viewModel.ViewMode = LibraryViewMode.List;

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task GameDetailsPage_realises_with_achievements()
    {
        using var host = new TestAppHost();
        host.SeedSampleData();

        var games = await host.Resolve<IGameRepository>().GetAllAsync();
        Assert.NotEmpty(games);

        var viewModel = host.Resolve<GameDetailsViewModel>();
        await viewModel.InitializeAsync(games[0].Id);
        await viewModel.OnNavigatedToAsync();

        Assert.NotEmpty(viewModel.AchievementItems);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task HomePage_realises_its_recently_played_row()
    {
        using var host = new TestAppHost();
        host.SeedSampleData();

        var viewModel = host.Resolve<HomeViewModel>();
        await viewModel.LoadAsync();

        // The tile template only instantiates when there are tiles.
        Assert.True(viewModel.HasLibrary);
        Assert.NotEmpty(viewModel.RecentGames);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task HomePage_realises_its_empty_state()
    {
        using var host = new TestAppHost();

        var viewModel = host.Resolve<HomeViewModel>();
        await viewModel.LoadAsync();

        // A different branch of the markup from the populated one.
        Assert.False(viewModel.HasLibrary);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task AchievementsPage_realises_every_achievement_state()
    {
        using var host = new TestAppHost();

        // Seeded so that one row of each state is templated: unlocked, plainly
        // locked, locked with progress, hidden, and one whose provider is gone.
        // An empty list would realise none of them.
        await SeedAchievementStatesAsync(host);

        var viewModel = host.Resolve<AchievementsViewModel>();
        await viewModel.LoadAsync();

        Assert.NotEmpty(viewModel.Groups);
        Assert.Equal(5, viewModel.Groups.SelectMany(group => group.Items).Count());

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task AchievementsPage_realises_when_the_library_has_none()
    {
        using var host = new TestAppHost();

        var viewModel = host.Resolve<AchievementsViewModel>();
        await viewModel.LoadAsync();

        // The empty state is a different branch of the markup from the list.
        Assert.False(viewModel.HasAny);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task DiscoverPage_realises_with_listings_in_every_display_state()
    {
        using var host = new TestAppHost();

        // Item templates only instantiate when their control has items, so a row
        // of each state is seeded: installable, restricted, with and without a
        // year, and one with no developer to attribute it to.
        await host.Resolve<GameLauncher.Desktop.Services.Database.ICatalogListingRepository>().UpsertManyAsync(
        [
            DiscoverListing("lst_1", "Doom", 1993, downloadable: true, developer: "id Software"),
            DiscoverListing("lst_2", "Oregon Trail", 1990, downloadable: false, developer: "MECC"),
            DiscoverListing("lst_3", "Untitled", null, downloadable: true, developer: null)
        ]);

        var viewModel = host.Resolve<DiscoverViewModel>();

        // Set before navigating so the first query already includes the
        // restricted listing; its tile draws the disabled-install branch, which
        // the default filter would hide.
        viewModel.DownloadableOnly = false;

        await viewModel.OnNavigatedToAsync();

        Assert.Equal(3, viewModel.ListingsView.Count);
        Assert.False(viewModel.IsCatalogEmpty);
        Assert.Contains(viewModel.ListingsView, item => !item.IsDownloadable);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task DiscoverPage_hides_listings_that_cannot_be_installed_by_default()
    {
        using var host = new TestAppHost();

        await host.Resolve<GameLauncher.Desktop.Services.Database.ICatalogListingRepository>().UpsertManyAsync(
        [
            DiscoverListing("lst_1", "Doom", 1993, downloadable: true, developer: "id Software"),
            DiscoverListing("lst_2", "Oregon Trail", 1990, downloadable: false, developer: "MECC")
        ]);

        var viewModel = host.Resolve<DiscoverViewModel>();
        await viewModel.OnNavigatedToAsync();

        Assert.Single(viewModel.ListingsView);
        Assert.Equal("Doom", viewModel.ListingsView[0].Title);
    }

    [Fact]
    public async Task DownloadsPage_realises_a_row_in_every_control_state()
    {
        using var host = new TestAppHost();

        // A row of each state, so every branch of the row template instantiates:
        // one running with controls, one paused, one failed with a retry, and one
        // finished and waiting to be installed.
        var queue = host.Resolve<GameLauncher.Desktop.Services.Downloads.IDownloadQueue>();

        foreach (var index in Enumerable.Range(1, 4))
        {
            queue.Enqueue($"lst_{index}", $"Game {index}");
        }

        var jobs = queue.Jobs;

        queue.Pause(jobs[1].JobId);
        queue.Cancel(jobs[2].JobId);

        jobs[3].Phase = GameLauncher.Desktop.Models.DownloadPhase.ReadyToInstall;

        var viewModel = host.Resolve<DownloadsViewModel>();
        await viewModel.OnNavigatedToAsync();

        Assert.Equal(4, viewModel.Items.Count);
        Assert.False(viewModel.IsEmpty);

        _wpf.Invoke(() => RealisePage(viewModel));

        await viewModel.OnNavigatedFromAsync();
    }

    [Fact]
    public async Task DownloadsPage_realises_its_empty_state()
    {
        using var host = new TestAppHost();

        var viewModel = host.Resolve<DownloadsViewModel>();
        await viewModel.OnNavigatedToAsync();

        Assert.True(viewModel.IsEmpty);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task DiscoverPage_realises_its_empty_state()
    {
        using var host = new TestAppHost();

        var viewModel = host.Resolve<DiscoverViewModel>();
        await viewModel.OnNavigatedToAsync();

        // A different branch of the markup from the tile list.
        Assert.True(viewModel.IsCatalogEmpty);
        Assert.False(viewModel.IsDiscoveryEnabled);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    private static GameLauncher.Desktop.Models.CatalogListing DiscoverListing(
        string id,
        string title,
        int? year,
        bool downloadable,
        string? developer) =>
        new()
        {
            ListingId = id,
            Title = title,
            SortTitle = title,
            Year = year,
            Developer = developer,
            MatchKey = $"{title.ToLowerInvariant()}|{year ?? 0}",
            PrimarySourceKey = "test",
            ContentHash = id,
            IsDownloadable = downloadable,
            Genres = ["Action"]
        };

    [Fact]
    public async Task AchievementEditorWindow_realises_each_rule_panel()
    {
        using var host = new TestAppHost();
        await SeedAchievementStatesAsync(host);

        // Each provider shows a different rule panel, so every one is selected in
        // turn against a realised window.
        foreach (var providerKey in host.Resolve<IAchievementEngine>().Providers.Select(p => p.Key))
        {
            // Constructed on the WPF thread — a Window cannot be built anywhere
            // else — but loaded from this one, because the view model awaits with
            // ConfigureAwait(true) and blocking the dispatcher on it would
            // deadlock against its own continuation.
            AchievementEditorWindow window = null!;
            AchievementEditorViewModel viewModel = null!;

            _wpf.Invoke(() =>
            {
                window = host.Resolve<AchievementEditorWindow>();
                viewModel = (AchievementEditorViewModel)window.DataContext;
                viewModel.Initialize(null);
            });

            await viewModel.OnNavigatedToAsync();

            _wpf.Invoke(() =>
            {
                viewModel.SelectedProvider = viewModel.Providers.Single(p => p.Key == providerKey);
                Realise(window);
            });
        }
    }

    [Fact]
    public async Task AchievementEditorWindow_realises_its_missing_provider_banner()
    {
        using var host = new TestAppHost();

        var catalog = host.Resolve<ICatalogService>();
        var achievements = host.Resolve<IAchievementRepository>();
        var entry = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        var id = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_IMPORTED",
            Title = "Imported",
            ProviderKey = "steam-import"
        });

        var definition = await achievements.GetDefinitionByIdAsync(id);

        AchievementEditorWindow window = null!;
        AchievementEditorViewModel viewModel = null!;

        _wpf.Invoke(() =>
        {
            window = host.Resolve<AchievementEditorWindow>();
            viewModel = (AchievementEditorViewModel)window.DataContext;
            viewModel.Initialize(definition);
        });

        await viewModel.OnNavigatedToAsync();

        Assert.True(viewModel.IsProviderMissing);

        _wpf.Invoke(() => Realise(window));
    }

    [Fact]
    public void AchievementToast_realises_while_showing_an_achievement()
    {
        using var host = new TestAppHost();

        var notifications = new StubNotifications();
        using var viewModel = new AchievementToastHostViewModel(notifications, new ImmediateDispatcher());

        notifications.Publish(
            AchievementNotification.FromDefinition(
                new AchievementDefinition
                {
                    Id = 1,
                    ApiName = "ACH_TEST",
                    Title = "Getting started",
                    Description = "Launch the game for the first time."
                },
                new Game { Id = 1, Title = "Hollow Signal", Tags = [] },
                DateTimeOffset.Now),
            pending: 2);

        // Collapsed while nothing is showing, so the toast markup only realises
        // once there is something to announce.
        Assert.True(viewModel.IsVisible);

        _wpf.Invoke(() =>
        {
            var window = new Window
            {
                Width = 900,
                Height = 600,
                Content = new AchievementToastHost { DataContext = viewModel }
            };

            Realise(window);
        });
    }

    [Fact]
    public async Task SettingsPage_realises()
    {
        using var host = new TestAppHost();

        // Loaded so the page renders against a real identity rather than blanks.
        await host.Resolve<ISettingsService>().LoadAsync();

        var viewModel = host.Resolve<SettingsViewModel>();
        await viewModel.OnNavigatedToAsync();

        Assert.NotEmpty(viewModel.FriendCode);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task FriendsPage_realises_from_cache_with_no_relay_configured()
    {
        using var host = new TestAppHost();
        await host.Resolve<ISettingsService>().LoadAsync();

        // A cached friend, as the launcher would have after a previous session.
        await host.Resolve<IFriendCacheRepository>().UpsertAsync(new FriendCache
        {
            FriendCode = "GL-ABCDE-FGHJK",
            DisplayName = "Cached Friend",
            LastKnownGame = "Hollow Signal",
            LastSeenAt = DateTimeOffset.Now.AddHours(-3)
        });

        var friends = host.Resolve<IFriendsService>();
        await friends.LoadFromCacheAsync();

        var viewModel = host.Resolve<FriendsViewModel>();
        await viewModel.OnNavigatedToAsync();

        // The page renders from cache with no relay configured and no network
        // touched, which is the offline-first requirement.
        Assert.NotEmpty(viewModel.Entries);
        Assert.Equal(RelayConnectionState.Disabled, viewModel.ConnectionState);
        Assert.True(viewModel.IsUsingCachedData);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task CollectionsPage_realises_with_membership()
    {
        using var host = new TestAppHost();
        host.SeedSampleData();

        var viewModel = host.Resolve<CollectionsViewModel>();
        await viewModel.LoadAsync();

        Assert.NotEmpty(viewModel.CollectionItems);

        _wpf.Invoke(() => RealisePage(viewModel));
    }

    [Fact]
    public async Task Every_palette_supplies_every_key_the_views_need()
    {
        using var outer = new TestAppHost();
        var theme = outer.Resolve<IThemeService>();

        try
        {
            // A palette missing a key that another palette defines throws only
            // when a view referencing it is realised, so each theme is applied and
            // the heaviest views rebuilt under it.
            foreach (var candidate in Enum.GetValues<AppTheme>())
            {
                // A fresh container per theme, because MainWindow is registered as
                // a singleton — correct for the application, which has exactly one
                // shell — and WPF refuses to show a window that has been closed.
                using var host = new TestAppHost();
                host.SeedSampleData();

                var library = host.Resolve<LibraryViewModel>();
                await library.LoadAsync();

                // The discovery page is realised here too, with a row seeded so
                // its item template actually instantiates. A key it uses that one
                // palette omits would otherwise only throw the first time someone
                // opened Discover on that theme.
                await host.Resolve<GameLauncher.Desktop.Services.Database.ICatalogListingRepository>()
                    .UpsertManyAsync([DiscoverListing("lst_p", "Doom", 1993, true, "id Software")]);

                var discover = host.Resolve<DiscoverViewModel>();
                await discover.OnNavigatedToAsync();

                // The shell is navigated before it is shown, so its sub-navigation
                // strip has the four Library tabs in it. Realised empty, the strip
                // would never expand its item container template and a palette
                // missing a key it uses would go on passing.
                var shell = host.Resolve<MainWindowViewModel>();
                await shell.InitializeAsync();

                Assert.True(shell.HasSubSections);

                // Settings is here because it is the densest page in the
                // application — every input style the theme defines appears on
                // it, and most of them appear nowhere else. Realised only under
                // the default theme, a caption or text box style that one
                // palette omits would first throw for whoever had chosen that
                // palette and then opened Settings.
                await host.Resolve<ISettingsService>().LoadAsync();

                var settings = host.Resolve<SettingsViewModel>();
                await settings.OnNavigatedToAsync();

                _wpf.Invoke(() =>
                {
                    theme.Apply(candidate);
                    Realise(host.Resolve<MainWindow>());
                    RealisePage(library);
                    RealisePage(discover);
                    RealisePage(settings);
                    Realise(host.Resolve<InstallFromUrlWindow>());
                });
            }
        }
        finally
        {
            // Restored so the shared WPF host does not leak a palette into
            // whichever test runs next.
            _wpf.Invoke(() => theme.Apply(AppTheme.Dark));
        }
    }

    /// <summary>
    /// Hosts a page view model in a window so the application's view templates
    /// select and realise its view.
    /// </summary>
    /// <param name="pageViewModel">The page to realise.</param>
    private static void RealisePage(ViewModelBase pageViewModel)
    {
        var window = new Window
        {
            Width = 1280,
            Height = 800,

            // Bound through a ContentControl exactly as the shell does, so the
            // DataTemplate lookup under test is the real one.
            Content = new ContentControl { Content = pageViewModel }
        };

        Realise(window);
    }

    /// <summary>
    /// Shows a window off screen, forces a full layout pass, and closes it.
    /// </summary>
    /// <param name="window">The window to realise.</param>
    /// <remarks>
    /// Shown rather than merely measured because some control templates are only
    /// expanded once a window has a presentation source. Positioned far off
    /// screen so a test run does not flash windows at whoever is watching.
    /// </remarks>
    private static void Realise(Window window)
    {
        PositionOffScreen(window);

        try
        {
            window.Show();
            window.UpdateLayout();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Places a window far off screen so a test run does not flash windows at
    /// whoever is watching.
    /// </summary>
    /// <param name="window">The window about to be shown.</param>
    private static void PositionOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.Left = -32000;
        window.Top = -32000;
    }

    /// <summary>
    /// Seeds one achievement in each display state.
    /// </summary>
    /// <param name="host">The container to seed through.</param>
    /// <returns>A task that completes once the rows exist.</returns>
    /// <remarks>
    /// Every branch of the achievement template is reached by some row here.
    /// Realising a page whose rows are all in one state would leave the rest of
    /// the markup — the progress bar, the concealed row, the provider warning —
    /// untested.
    /// </remarks>
    private static async Task SeedAchievementStatesAsync(TestAppHost host)
    {
        var catalog = host.Resolve<ICatalogService>();
        var games = host.Resolve<IGameRepository>();
        var achievements = host.Resolve<IAchievementRepository>();

        var entry = await catalog.EnsureEntryAsync("Hollow Signal", executable: null);

        await games.AddAsync(new Game
        {
            Title = "Hollow Signal",
            CatalogId = entry.CatalogId,
            ExecutablePath = @"C:\Games\Hollow\game.exe",
            DateAdded = DateTimeOffset.Now,
            PlaytimeSeconds = 4 * 3600,
            LastPlayedAt = DateTimeOffset.Now,
            Tags = []
        });

        var unlockedId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_UNLOCKED",
            Title = "First steps",
            Description = "Launch the game once.",
            ProviderKey = MetaAchievementProvider.ProviderKey,
            SortOrder = 0
        });

        await achievements.UnlockAsync(unlockedId, DateTimeOffset.Now.AddHours(-2));

        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_LOCKED",
            Title = "Still to come",
            Description = "Finish the campaign.",
            ProviderKey = MetaAchievementProvider.ProviderKey,
            SortOrder = 1
        });

        var progressId = await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_PROGRESS",
            Title = "Ten hours in",
            Description = "Play for ten hours.",
            ProviderKey = MetaAchievementProvider.ProviderKey,
            ProgressTarget = 10,
            SortOrder = 2
        });

        await achievements.RecordProgressAsync(progressId, 4, DateTimeOffset.Now);

        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_HIDDEN",
            Title = "The secret ending",
            Description = "Reach the observatory before dawn.",
            IsHidden = true,
            ProviderKey = MetaAchievementProvider.ProviderKey,
            SortOrder = 3
        });

        await achievements.AddDefinitionAsync(new AchievementDefinition
        {
            CatalogId = entry.CatalogId,
            ApiName = "ACH_ORPHAN",
            Title = "Imported from elsewhere",
            ProviderKey = "steam-import",
            SortOrder = 4
        });
    }

    /// <summary>
    /// A notifier the test drives directly, so the toast can be realised without
    /// waiting on the real dwell timer.
    /// </summary>
    private sealed class StubNotifications : IAchievementNotificationService
    {
        public event EventHandler<AchievementNotificationChangedEventArgs>? CurrentChanged;

        public AchievementNotification? Current { get; private set; }

        public int PendingCount { get; private set; }

        public void DismissCurrent() => Publish(null, 0);

        public void Publish(AchievementNotification? notification, int pending)
        {
            Current = notification;
            PendingCount = pending;
            CurrentChanged?.Invoke(this, new AchievementNotificationChangedEventArgs(notification, pending));
        }
    }

    /// <summary>Builds a representative scan result for template realisation.</summary>
    /// <returns>A discovered game pointing at a path that need not exist.</returns>
    private static DiscoveredGame SampleDiscovery() => new()
    {
        Executable = new ExecutableInfo(
            Path: @"C:\Games\Sample\sample.exe",
            FileName: "sample.exe",
            SuggestedTitle: "Sample Game",
            ProductName: "Sample Game",
            FileDescription: "Sample",
            CompanyName: "Sample Studio",
            FileVersion: "1.0.0.0",
            FileSizeBytes: 4 * 1024 * 1024,
            Architecture: ExecutableArchitecture.X64,
            Subsystem: ExecutableSubsystem.WindowsGui,
            IsValidExecutable: true),
        InstallDirectory = @"C:\Games\Sample",
        IsLikelyGame = true,
        IsAlreadyInLibrary = false,
        Note = null
    };
}
