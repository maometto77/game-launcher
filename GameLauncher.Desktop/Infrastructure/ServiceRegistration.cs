using GameLauncher.Desktop.Infrastructure.Navigation;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Artwork;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Desktop.Services.Friends;
using GameLauncher.Desktop.Services.Launcher;
using GameLauncher.Desktop.Services.Library;
using GameLauncher.Desktop.Services.Notifications;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Desktop.ViewModels;
using GameLauncher.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Registers the application's services and view models with the DI container.
/// </summary>
/// <remarks>
/// Composition lives in one place so that the object graph can be read start to
/// finish, and so a test host can register the same graph with selected
/// services substituted.
/// </remarks>
public static class ServiceRegistration
{
    /// <summary>
    /// Adds every launcher service, view model and window to the container.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="paths">Resolved application paths, created before the host so logging can use them.</param>
    /// <param name="options">Options parsed from the command line.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddGameLauncher(
        this IServiceCollection services,
        IAppPaths paths,
        StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(paths);
        services.AddSingleton(options);

        AddInfrastructure(services);
        AddDataAccess(services);
        AddApplicationServices(services);
        AddViewModels(services);
        AddViews(services);

        return services;
    }

    /// <summary>Registers cross-cutting infrastructure.</summary>
    /// <param name="services">The container being built.</param>
    private static void AddInfrastructure(IServiceCollection services)
    {
        services.AddSingleton<IStartupNotices, StartupNotices>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUiDispatcher, UiDispatcher>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IWindowService, WindowService>();

        services.AddSingleton<IIdentityGenerator, IdentityGenerator>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();

        // The one place a dialog's view model is tied to its window, so that no
        // view model ever needs to name a Window type.
        services.AddSingleton(new DialogRegistry()
            .Register<AddGameViewModel, AddGameWindow>()
            .Register<ScanFolderViewModel, ScanFolderWindow>()
            .Register<InstallFromUrlViewModel, InstallFromUrlWindow>()
            .Register<AchievementEditorViewModel, AchievementEditorWindow>());

        // Hosted services start in registration order, and this one must come
        // first: the theme has to be applied before any window is constructed.
        services.AddHostedService<SettingsStartupService>();

        // Migrates the schema before the shell window is shown.
        services.AddHostedService<DatabaseStartupService>();

        // One object in two roles: the notifier the toast overlay binds to, and a
        // hosted service so it is subscribed to the engine before the watcher
        // below runs its startup pass. Subscribing later would silently drop any
        // achievement that pass earns.
        services.AddSingleton<AchievementNotificationService>();
        services.AddSingleton<IAchievementNotificationService>(
            provider => provider.GetRequiredService<AchievementNotificationService>());
        services.AddHostedService(provider => provider.GetRequiredService<AchievementNotificationService>());

        // Decides when evaluation runs. Kept separate from the engine so that
        // scheduling can change without touching a single rule.
        services.AddHostedService<AchievementWatcherService>();

        // Last: registration, connection and sync all depend on settings and the
        // database already being ready. Nothing here can block startup.
        services.AddHostedService<RelayCoordinatorService>();
    }

    /// <summary>
    /// Registers the application-logic services that sit between the view models
    /// and the repositories.
    /// </summary>
    /// <param name="services">The container being built.</param>
    private static void AddApplicationServices(IServiceCollection services)
    {
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<IExecutableInspector, ExecutableInspector>();
        services.AddSingleton<IIconExtractionService, IconExtractionService>();
        services.AddSingleton<IGameScanService, GameScanService>();
        services.AddSingleton<IGameImportService, GameImportService>();

        // Relay networking. Both clients are singletons: the hub owns a live
        // connection, and the friends service holds the merged list every page
        // binds against.
        services.AddSingleton<IRelayApiClient, RelayApiClient>();
        services.AddSingleton<IRelayHubClient, SignalRRelayHubClient>();
        services.AddSingleton<IFriendsService, FriendsService>();
        services.AddSingleton<IRelaySyncService, RelaySyncService>();
        services.AddSingleton<IRelayIdentityService, RelayIdentityService>();

        // A short timeout, unlike the download client: relay calls are small, and
        // an offline-first launcher should conclude "unreachable" quickly rather
        // than leaving the user waiting.
        services.AddHttpClient(RelayApiClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        // Achievements. Providers are registered as an open set: the engine
        // resolves IEnumerable<IAchievementProvider> and dispatches by key, so
        // adding one here is the entire cost of adding a provider.
        services.AddSingleton<ISaveFileReader, SaveFileReader>();
        services.AddSingleton<IProcessMemoryReader, ProcessMemoryReader>();

        services.AddSingleton<IAchievementProvider, MetaAchievementProvider>();
        services.AddSingleton<IAchievementProvider, SaveFileAchievementProvider>();
        services.AddSingleton<IAchievementProvider, MemoryAchievementProvider>();
        services.AddSingleton<IAchievementProvider, ManualAchievementProvider>();

        services.AddSingleton<IAchievementEngine, AchievementEngine>();

        // Artwork lookup. The provider is a seam so a second source can be added
        // without touching the service that downloads and applies the result.
        services.AddSingleton<IArtworkProvider, SteamGridDbArtworkProvider>();
        services.AddSingleton<IArtworkService, ArtworkService>();

        // A short timeout like the relay client, not the download client's
        // infinite one: artwork requests are small, and a slow artwork lookup
        // should give up rather than leave a button spinning.
        services.AddHttpClient(SteamGridDbArtworkProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IArchiveExtractionService, ArchiveExtractionService>();
        services.AddSingleton<IInstallFromUrlService, InstallFromUrlService>();

        // The transfer client's timeout is disabled deliberately. HttpClient's
        // default 100 seconds covers the whole operation including the response
        // body, so a multi-gigabyte download would be aborted mid-transfer.
        // Cancellation is handled by the caller's token instead.
        services.AddHttpClient(DownloadService.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        // Singleton because it owns the set of running processes; a transient
        // would lose track of every game already in flight.
        services.AddSingleton<IGameLaunchService, GameLaunchService>();
    }

    /// <summary>
    /// Registers the database layer.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <remarks>
    /// Repositories are singletons because they hold no per-call state — each
    /// method opens and disposes its own connection — so there is nothing to
    /// gain from constructing them repeatedly.
    /// </remarks>
    private static void AddDataAccess(IServiceCollection services)
    {
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();

        services.AddSingleton<IGameRepository, GameRepository>();
        services.AddSingleton<ICollectionRepository, CollectionRepository>();
        services.AddSingleton<IAchievementRepository, AchievementRepository>();
        services.AddSingleton<IFriendCacheRepository, FriendCacheRepository>();
        services.AddSingleton<IPlaySessionRepository, PlaySessionRepository>();

        services.AddSingleton<ISampleDataSeeder, SampleDataSeeder>();

        // Shared catalog identity: the anchor every cross-user feature keys on.
        services.AddSingleton<ICatalogRepository, CatalogRepository>();
        services.AddSingleton<ICatalogService, CatalogService>();
    }

    /// <summary>
    /// Registers view models.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <remarks>
    /// Page view models are transient so each navigation starts from a clean
    /// state. The shell view model is a singleton because it is the window's
    /// data context for the lifetime of the process.
    /// </remarks>
    private static void AddViewModels(IServiceCollection services)
    {
        services.AddSingleton<MainWindowViewModel>();

        // A singleton, like the shell that hosts it: an achievement can be earned
        // at any moment, so the overlay outlives whichever page is on screen.
        services.AddSingleton<AchievementToastHostViewModel>();

        services.AddTransient<HomeViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<GameDetailsViewModel>();
        services.AddTransient<CollectionsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<FriendsViewModel>();
        services.AddTransient<AchievementsViewModel>();

        // Dialog view models are transient: each opening starts from a blank form.
        services.AddTransient<AddGameViewModel>();
        services.AddTransient<ScanFolderViewModel>();
        services.AddTransient<InstallFromUrlViewModel>();
        services.AddTransient<AchievementEditorViewModel>();
    }

    /// <summary>Registers windows.</summary>
    /// <param name="services">The container being built.</param>
    private static void AddViews(IServiceCollection services)
    {
        services.AddSingleton<MainWindow>();

        // Dialog windows must be transient. A WPF window cannot be shown again
        // once it has been closed, so a singleton would throw the second time the
        // user opened the dialog.
        services.AddTransient<AddGameWindow>();
        services.AddTransient<ScanFolderWindow>();
        services.AddTransient<InstallFromUrlWindow>();
        services.AddTransient<AchievementEditorWindow>();
    }
}
