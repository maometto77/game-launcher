using System.Diagnostics;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Import;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the background refresh: that it never delays startup, that it does
/// nothing until asked, and that it stops when the host does.
/// </summary>
public sealed class BackgroundImportTests
{
    private static readonly CatalogRefreshSchedule Immediate =
        new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task Starting_never_waits_for_a_refresh()
    {
        // The whole point of the design: an unreachable source must cost the
        // person opening the launcher nothing at all.
        var source = new FakeCatalogSource { Throttle = new SourceThrottle(1, TimeSpan.FromSeconds(30)) };

        source.Add("Doom", 1993);

        using var host = Host(source, enabled: true);
        using var service = Service(host, new CatalogRefreshSchedule(TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        var stopwatch = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 500,
            $"StartAsync took {stopwatch.ElapsedMilliseconds}ms and must not block");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Nothing_is_imported_while_discovery_is_switched_off()
    {
        var source = new FakeCatalogSource();
        source.Add("Doom", 1993);

        using var host = Host(source, enabled: false);
        using var service = Service(host, Immediate);

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => source.EnumerateCount > 0, TimeSpan.FromMilliseconds(400));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, source.EnumerateCount);
        Assert.Equal(0, await host.Resolve<ICatalogListingRepository>().CountAsync());
    }

    [Fact]
    public async Task A_refresh_runs_once_discovery_is_switched_on()
    {
        var source = new FakeCatalogSource();
        source.Add("Doom", 1993).Add("SimCity", 1989);

        using var host = Host(source, enabled: true);
        using var service = Service(host, Immediate);

        await service.StartAsync(CancellationToken.None);

        var imported = await WaitForAsync(
            async () => await host.Resolve<ICatalogListingRepository>().CountAsync() == 2,
            TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        Assert.True(imported, "the background service should have imported both games");
    }

    [Fact]
    public async Task A_second_refresh_is_not_run_before_the_interval_elapses()
    {
        var source = new FakeCatalogSource();
        source.Add("Doom", 1993);

        using var host = Host(source, enabled: true);
        using var service = Service(host, Immediate);

        await service.StartAsync(CancellationToken.None);

        await WaitForAsync(
            async () => await host.Resolve<ICatalogListingRepository>().CountAsync() == 1,
            TimeSpan.FromSeconds(5));

        // The poll interval is 50ms but the refresh interval is a day, so the
        // loop wakes many times and declines to do anything each time.
        await WaitForAsync(() => source.EnumerateCount > 1, TimeSpan.FromMilliseconds(500));

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, source.EnumerateCount);
    }

    [Fact]
    public async Task Stopping_ends_the_loop()
    {
        var source = new FakeCatalogSource();
        source.Add("Doom", 1993);

        using var host = Host(source, enabled: true);
        using var service = Service(host, Immediate);

        await service.StartAsync(CancellationToken.None);

        await WaitForAsync(
            async () => await host.Resolve<ICatalogListingRepository>().CountAsync() == 1,
            TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        var afterStop = source.EnumerateCount;

        await WaitForAsync(() => source.EnumerateCount > afterStop, TimeSpan.FromMilliseconds(300));

        Assert.Equal(afterStop, source.EnumerateCount);
    }

    [Fact]
    public async Task An_interrupted_run_is_resumed_at_the_next_check()
    {
        var source = new FakeCatalogSource();
        source.Add("Doom", 1993);

        using var host = Host(source, enabled: true);
        var repository = host.Resolve<ICatalogListingRepository>();

        // An unfinished run is the residue of a process killed mid-pass. Its
        // cursor is exactly where to carry on from, so it is picked up straight
        // away rather than waiting a full refresh interval.
        await repository.StartRunAsync(FakeSourceKey, GameLauncher.Desktop.Models.ImportMode.Incremental);

        using var service = Service(host, Immediate);

        await service.StartAsync(CancellationToken.None);

        var resumed = await WaitForAsync(() => source.EnumerateCount > 0, TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        Assert.True(resumed, "an unfinished run should be resumed without waiting for the interval");
    }

    [Fact]
    public async Task A_failing_source_does_not_kill_the_loop()
    {
        var source = new ThrowingSource();

        using var host = Host(source, enabled: true);
        using var service = Service(host, Immediate);

        await service.StartAsync(CancellationToken.None);

        // Several poll cycles: if the first failure killed the loop, the count
        // would stop at one.
        await WaitForAsync(() => source.Attempts >= 3, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.True(source.Attempts >= 2, $"the loop stopped after {source.Attempts} attempt(s)");
    }

    private const string FakeSourceKey = "fake";

    private static CatalogImportBackgroundService Service(TestAppHost host, CatalogRefreshSchedule schedule) =>
        new(
            host.Resolve<ICatalogImportService>(),
            host.Resolve<ICatalogListingRepository>(),
            host.Resolve<ISettingsService>(),
            NullLogger<CatalogImportBackgroundService>.Instance,
            schedule);

    private static TestAppHost Host(ICatalogSource source, bool enabled)
    {
        var host = new TestAppHost(null, migrate: true, configure: services =>
        {
            services.AddSingleton(source);

            // Saving settings raises SettingsChanged through IUiDispatcher, and
            // the real one binds to Application.Current.Dispatcher whenever a WPF
            // Application exists anywhere in the process. That dispatcher belongs
            // to the WPF test collection and is only pumped while that collection
            // runs, so a settings save from here would wait on a message loop
            // that is not running. The same reason AchievementToastTests
            // substitutes it.
            services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        });

        var settings = host.Resolve<ISettingsService>();

        settings.SaveAsync(settings.Current with
        {
            DiscoveryEnabled = enabled,
            DiscoveryRefreshHours = 24,

            // Switching discovery on makes every configured source available,
            // and the real Internet Archive source is registered in the same
            // container. Clearing its collections is what keeps these tests off
            // the network — the fake source is the only one left available.
            InternetArchiveCollections = []
        }).GetAwaiter().GetResult();

        return host;
    }

    /// <summary>Polls a condition until it holds or the deadline passes.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout) =>
        await WaitForAsync(() => Task.FromResult(condition()), timeout);

    /// <summary>Polls an asynchronous condition until it holds or the deadline passes.</summary>
    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>A source whose enumeration always fails.</summary>
    private sealed class ThrowingSource : ICatalogSource
    {
        private int _attempts;

        public string Key => FakeSourceKey;

        public string DisplayName => "Throwing";

        public int Rank => 0;

        public SourceThrottle Throttle => new(1, TimeSpan.Zero);

        public bool IsAvailable => true;

        public int Attempts => Volatile.Read(ref _attempts);

        public async IAsyncEnumerable<SourceListingRef> EnumerateAsync(
            SourceEnumerationOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);

            await Task.Yield();

            throw new InvalidOperationException("simulated source failure");

#pragma warning disable CS0162 // Required to make this an iterator.
            yield break;
#pragma warning restore CS0162
        }

        public Task<SourceListing?> FetchAsync(
            SourceListingRef reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceListing?>(null);
    }
}
