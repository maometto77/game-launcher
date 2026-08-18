using GameLauncher.Desktop.Infrastructure.Navigation;
using GameLauncher.Desktop.Services.Achievements;
using GameLauncher.Desktop.Services.Achievements.Emulators;
using GameLauncher.Desktop.Services.Achievements.Providers;
using GameLauncher.Desktop.Services.Artwork;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Dialogs;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Images;
using GameLauncher.Desktop.Services.Discovery.Import;
using GameLauncher.Desktop.Services.Discovery.Install;
using GameLauncher.Desktop.Services.Discovery.Matching;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Desktop.Services.Discovery.Sourcing;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;
using GameLauncher.Desktop.Services.Discovery.Sources;
using GameLauncher.Desktop.Services.Discovery.Sources.SharedCatalog;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Desktop.Services.Downloads;
using GameLauncher.Desktop.Services.Friends;
using GameLauncher.Desktop.Services.Launcher;
using GameLauncher.Desktop.Services.Library;
using GameLauncher.Desktop.Services.Notifications;
using GameLauncher.Desktop.Services.Saves;
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

        // Finds the external programs the launcher shells out to: bundled
        // beside the executable, dropped into a tools folder, or already on
        // PATH. It resolves and never installs — nothing here downloads a
        // program.
        services.AddSingleton<IExternalToolLocator, ExternalToolLocator>();

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

        // Reads the achievement files Steam emulators leave on disk. Registered
        // before the notifier because the notifier subscribes to it, and as one
        // object in two roles so the interface and the hosted lifetime are the
        // same instance rather than two watchers over the same folders.
        services.AddSingleton<AchievementWatcherService>();
        services.AddSingleton<IAchievementWatcherService>(
            provider => provider.GetRequiredService<AchievementWatcherService>());
        services.AddHostedService(provider => provider.GetRequiredService<AchievementWatcherService>());

        // One object in two roles: the notifier the toast overlay binds to, and a
        // hosted service so it is subscribed to both publishers before either
        // runs its startup pass. Subscribing later would silently drop any
        // achievement those passes find.
        services.AddSingleton<AchievementNotificationService>();
        services.AddSingleton<IAchievementNotificationService>(
            provider => provider.GetRequiredService<AchievementNotificationService>());
        services.AddHostedService(provider => provider.GetRequiredService<AchievementNotificationService>());

        // Decides when evaluation runs. Kept separate from the engine so that
        // scheduling can change without touching a single rule.
        services.AddHostedService<AchievementScheduler>();

        // Registration, connection and sync all depend on settings and the
        // database already being ready. Nothing here can block startup.
        services.AddHostedService<RelayCoordinatorService>();

        // Last of all, and for the same reason as the relay: it depends on
        // settings and the database, nothing depends on it, and a slow or
        // unreachable source must never be felt as the launcher starting slowly.
        services.AddHostedService<CatalogImportBackgroundService>();
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

        // Discovery. Normalisation, matching and merging are pure — no database,
        // no network, no clock — which is what lets every interesting rule be
        // tested against a captured payload instead of a live site.
        services.AddSingleton<IListingNormalizer, ListingNormalizer>();
        services.AddSingleton<IListingMatcher, ListingMatcher>();
        services.AddSingleton<IListingMerger, ListingMerger>();

        // Sources are an open set dispatched by key, like achievement providers:
        // adding one is a class and a line here. The import service throws at
        // construction if two ever claim the same key.
        services.AddSingleton<ICatalogSource, InternetArchiveCatalogSource>();
        services.AddSingleton<ICatalogSource, MyAbandonwareCatalogSource>();

        // The one source that is not somebody else's website: a document a group
        // publishes for itself, usually beside the files it describes. Inert
        // until a feed address is configured, so it costs nothing to register.
        services.AddSingleton<ICatalogSource, SharedCatalogSource>();

        // Honouring robots.txt is what separates importing a catalogue from
        // taking whatever a server will serve. Shared, and cached per host.
        services.AddSingleton<IRobotsPolicy, RobotsPolicy>();

        services.AddHttpClient(RobotsPolicy.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        services.AddHttpClient(MyAbandonwareCatalogSource.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        services.AddHttpClient(SharedCatalogSource.HttpClientName, client =>
        {
            // One request returns the whole catalogue, so this is generous where
            // the per-item sources are not: a feed of several thousand entries is
            // a large document, and it may be served by a small machine reading
            // it off a disk that has spun down.
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        services.AddSingleton<ICatalogImportService, CatalogImportService>();

        // A translator, not a second download stack: it picks an address and
        // hands the existing install path a request it already understands.
        // Sourcing adapters answer "given this page, what can be downloaded" —
        // a different question from "what games exist", with different failure
        // modes, which is why they are a separate open set from ICatalogSource.
        services.AddSingleton<ISourcingAdapter, InternetArchiveSourcingAdapter>();
        services.AddSingleton<ISourcingAdapter, MyAbandonwareSourcingAdapter>();

        // The user's own feeds. One adapter serving any number of manifests
        // dropped into the adapter directory, so adding a source is a file
        // rather than a class — the feeds worth having are the ones nobody here
        // has heard of.
        services.AddSingleton<IFeedManifestStore, FeedManifestStore>();
        services.AddSingleton<IScriptHookRunner, ScriptHookRunner>();
        services.AddSingleton<ISourcingAdapter, ScriptableSourcingAdapter>();

        services.AddHttpClient(ScriptableSourcingAdapter.HttpClientName, client =>
        {
            // A feed is a small document from a host this launcher knows nothing
            // about. Short enough that an unresponsive one does not hold up an
            // install, generous enough for a home server waking a disk.
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        services.AddSingleton<IDownloadSourceResolver, DownloadSourceResolver>();

        services.AddSingleton<IListingInstallService, ListingInstallService>();

        // The queue is a singleton because it *is* the state: the list of
        // downloads and which are running outlives any page that shows them.
        services.AddSingleton<IDownloadQueue, DownloadQueue>();

        // Catalogue artwork is fetched when something displays it, never during
        // an import: several thousand listings with half a dozen images each
        // would be tens of thousands of transfers for pictures nobody looked at.
        services.AddSingleton<IListingImageCache, ListingImageCache>();

        services.AddHttpClient(ListingImageCache.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        // A generous but finite timeout. Metadata responses are small; a search
        // page over a large collection is not, and the download client's infinite
        // timeout would be wrong here — a stalled catalogue request should give
        // up rather than hold a background pass open indefinitely.
        services.AddHttpClient(InternetArchiveCatalogSource.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        // Transports are an open set chosen by capability and availability. The
        // HttpClient one is always available and always last, so an external
        // engine that is not installed simply is not selected.
        // Where a game keeps its saves, from the Ludusavi community manifest.
        // A data dependency rather than hardcoded paths: the knowledge is large,
        // changes constantly, and is already curated by people who care about it.
        services.AddSingleton<ISavePathResolver, LudusaviSavePathResolver>();

        services.AddHttpClient(LudusaviSavePathResolver.HttpClientName, client =>
        {
            // The manifest is tens of megabytes, so this is not a short request.
            client.Timeout = TimeSpan.FromMinutes(3);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLauncher/1.0");
        });

        services.AddSingleton<IDownloadTransport, Aria2DownloadTransport>();
        services.AddSingleton<IDownloadTransport, HttpDownloadTransport>();

        // Statistics from the aria2c that is currently running, over loopback.
        // The timeout is short on purpose. A poll that outlives the interval
        // between polls is worthless, and a missed sample costs nothing because
        // the next one is half a second away — whereas a slow one holds up the
        // whole reporting loop. One second is a very long time for a call to a
        // process on this machine.
        services.AddHttpClient(Aria2RpcClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(1);
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
        services.AddSingleton<IExternalAchievementRepository, ExternalAchievementRepository>();
        services.AddSingleton<IFriendCacheRepository, FriendCacheRepository>();
        services.AddSingleton<IPlaySessionRepository, PlaySessionRepository>();

        services.AddSingleton<ISampleDataSeeder, SampleDataSeeder>();

        // Shared catalog identity: the anchor every cross-user feature keys on.
        services.AddSingleton<ICatalogRepository, CatalogRepository>();
        services.AddSingleton<ICatalogService, CatalogService>();

        // The discovery catalogue, which is a different thing entirely: what
        // games exist, rather than which title an installed game is a copy of.
        // The two aggregates share nothing but Game.ListingId.
        services.AddSingleton<ICatalogListingRepository, CatalogListingRepository>();
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
        services.AddTransient<DiscoverViewModel>();
        services.AddTransient<DownloadsViewModel>();

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
