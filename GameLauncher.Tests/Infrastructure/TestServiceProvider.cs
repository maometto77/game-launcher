using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Builds the application's real dependency-injection container against a
/// throwaway state directory.
/// </summary>
/// <remarks>
/// Uses <see cref="ServiceRegistration.AddGameLauncher"/> rather than
/// re-registering a hand-picked subset, so a service that is missing from the
/// real composition root fails these tests too. Every path is redirected under a
/// temporary folder, which is what keeps a test run from touching the developer's
/// own library.
/// </remarks>
public sealed class TestAppHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private bool _disposed;

    private readonly bool _ownsDirectory;

    /// <summary>
    /// Creates a container and migrates a fresh database beneath a temporary root.
    /// </summary>
    public TestAppHost()
        : this(null)
    {
    }

    /// <summary>
    /// Creates a container over an existing state directory.
    /// </summary>
    /// <param name="rootDirectory">
    /// Directory to use, or <see langword="null"/> for a fresh temporary one.
    /// </param>
    /// <remarks>
    /// Reusing a directory is how a launcher restart is simulated: a second
    /// container over the same database, with none of the first one's in-memory
    /// state. Anything that survives is genuinely persisted.
    /// </remarks>
    public TestAppHost(string? rootDirectory)
    {
        _ownsDirectory = rootDirectory is null;

        RootDirectory = rootDirectory ?? Path.Combine(
            Path.GetTempPath(),
            "GameLauncherTests",
            Guid.NewGuid().ToString("N"));

        var paths = new AppPaths(RootDirectory);
        paths.EnsureCreated();

        var services = new ServiceCollection();

        // Logging is normally configured by the host builder, which these tests
        // do not run, so a no-provider logger factory is registered instead.
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddGameLauncher(paths, new StartupOptions());

        _provider = services.BuildServiceProvider();

        // Migrated up front so anything resolved here can query real tables.
        // Run on the calling thread: the repositories await with
        // ConfigureAwait(false) throughout, so there is no context to deadlock on.
        _provider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>Gets the temporary directory holding this host's state.</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Fills the database with the standard sample library.
    /// </summary>
    /// <remarks>
    /// Item templates are only instantiated when their items control actually has
    /// items, so a view realised against an empty library exercises far less of
    /// the markup than one realised against a populated one.
    /// </remarks>
    public void SeedSampleData() =>
        _provider.GetRequiredService<ISampleDataSeeder>()
            .SeedAsync()
            .GetAwaiter()
            .GetResult();

    /// <summary>Gets the service provider.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>Resolves a required service.</summary>
    /// <typeparam name="T">Service type to resolve.</typeparam>
    /// <returns>The resolved instance.</returns>
    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>Disposes the container and deletes the temporary state directory.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _provider.Dispose();

        // A borrowed directory belongs to the caller, which may still be using it
        // to simulate a restart.
        if (!_ownsDirectory)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return;
        }

        try
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // SQLite may still hold the file briefly. Leaving a folder behind in
            // the system temp directory is not worth failing a passing test over.
        }
    }
}
