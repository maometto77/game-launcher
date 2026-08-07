using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Covers what happens when the library database cannot be read, and the switch
/// that keeps a test run away from a real one.
/// </summary>
/// <remarks>
/// Written after a damaged database left the launcher refusing to start, five
/// times in a row, showing a raw SQLite message and offering nothing to act on.
/// Losing a library to corruption is bad luck; being unable to open the
/// application afterwards was a defect.
/// </remarks>
public sealed class StartupRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_damaged_database_is_preserved_and_replaced_rather_than_stopping_startup()
    {
        // A working library first, so there is something real to lose.
        string databaseFile;

        using (var host = new TestAppHost(_root))
        {
            await host.Resolve<IGameRepository>().AddAsync(new Game
            {
                Title = "Kung Fu Panda",
                ExecutablePath = @"C:\Games\KFP\Game.exe",
                DateAdded = DateTimeOffset.Now,
                Tags = []
            });

            databaseFile = host.Resolve<IAppPaths>().DatabaseFile;
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Overwritten with bytes that are not a database at all. This is the
        // shape the real failure took: SQLITE_NOTADB or SQLITE_CORRUPT with not
        // one readable page, including the schema.
        await File.WriteAllTextAsync(databaseFile, new string('x', 8192));

        using var recovered = new TestAppHost(_root, migrate: false);
        var startup = recovered.Services.GetServices<IHostedService>().OfType<DatabaseStartupService>().Single();

        // Previously this threw and took the whole host down with it.
        await startup.StartAsync(CancellationToken.None);

        // The damaged file is kept. It is the user's data, however unreadable,
        // and deleting it is not ours to decide.
        var preserved = Directory.GetFiles(_root, "gamelauncher.db.corrupt-*");
        Assert.Single(preserved);
        Assert.Equal(8192, new FileInfo(preserved[0]).Length);

        // A working, empty library took its place.
        Assert.True(File.Exists(databaseFile));
        Assert.Empty(await recovered.Resolve<IGameRepository>().GetAllAsync());

        // And the user is told, rather than silently starting over.
        var notice = Assert.Single(recovered.Resolve<IStartupNotices>().Messages);
        Assert.Contains("damaged", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFileName(preserved[0]), notice, StringComparison.Ordinal);
        Assert.Contains("Installed games are untouched", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_healthy_database_is_left_completely_alone()
    {
        using (var host = new TestAppHost(_root))
        {
            await host.Resolve<IGameRepository>().AddAsync(new Game
            {
                Title = "CSI-3",
                ExecutablePath = @"C:\Games\CSI3\CSI3.exe",
                DateAdded = DateTimeOffset.Now,
                Tags = []
            });
        }

        using var reopened = new TestAppHost(_root, migrate: false);
        var startup = reopened.Services.GetServices<IHostedService>().OfType<DatabaseStartupService>().Single();

        await startup.StartAsync(CancellationToken.None);

        // Nothing moved aside, nothing announced, and the library survived.
        Assert.Empty(Directory.GetFiles(_root, "gamelauncher.db.corrupt-*"));
        Assert.Empty(reopened.Resolve<IStartupNotices>().Messages);
        Assert.Single(await reopened.Resolve<IGameRepository>().GetAllAsync());
    }

    [Theory]
    [InlineData(new string[] { "--state-dir", @"C:\temp\launcher" }, @"C:\temp\launcher")]
    [InlineData(new string[] { @"--state-dir=C:\temp\launcher" }, @"C:\temp\launcher")]
    [InlineData(new string[] { "--state-dir", @"""C:\temp\launcher""" }, @"C:\temp\launcher")]
    [InlineData(new string[] { "--seed-sample-data", "--state-dir", @"C:\temp\x" }, @"C:\temp\x")]
    public void The_state_directory_switch_is_read_in_the_forms_people_type(string[] args, string expected)
    {
        Assert.Equal(expected, StartupOptions.Parse(args).StateDirectory);
    }

    // Cast to object so each array is one argument. Without it the string[] is
    // taken as the params array itself, and attribute blobs do not allow the
    // covariant conversion to object[].
    [Theory]
    [InlineData((object)new string[0])]
    [InlineData((object)new string[] { "--seed-sample-data" })]

    // A trailing switch with nothing after it must not consume the next thing
    // that does not exist.
    [InlineData((object)new string[] { "--state-dir" })]
    [InlineData((object)new string[] { "--state-dir=" })]
    public void No_state_directory_means_the_default_location(string[] args)
    {
        Assert.Null(StartupOptions.Parse(args).StateDirectory);
    }

    [Fact]
    public void The_sample_data_switch_still_works_alongside_it()
    {
        var options = StartupOptions.Parse(["--state-dir", @"C:\temp\x", "--seed-sample-data"]);

        Assert.True(options.SeedSampleData);
        Assert.Equal(@"C:\temp\x", options.StateDirectory);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
