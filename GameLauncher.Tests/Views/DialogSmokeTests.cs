using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Friends;
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
    public void MainWindow_builds_and_lays_out()
    {
        using var host = new TestAppHost();
        _wpf.Invoke(() => Realise(host.Resolve<MainWindow>()));
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
    public void HomePage_realises()
    {
        using var host = new TestAppHost();
        _wpf.Invoke(() => RealisePage(host.Resolve<HomeViewModel>()));
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

                _wpf.Invoke(() =>
                {
                    theme.Apply(candidate);
                    Realise(host.Resolve<MainWindow>());
                    RealisePage(library);
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
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.Left = -32000;
        window.Top = -32000;

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
