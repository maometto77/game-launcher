using System.Windows.Threading;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Settings;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Covers the dispatcher hop that used to wedge the whole test run.
/// </summary>
/// <remarks>
/// <para>
/// The failure was not a failing test. <c>SettingsService.SaveAsync</c> awaits
/// its dispatcher to raise <c>SettingsChanged</c>, and the real dispatcher binds
/// to <c>Application.Current.Dispatcher</c> — which is process-wide. Once the
/// WPF collection's fixture had created one and later shut it down, any test in
/// any other collection that saved settings queued an operation nothing would
/// ever pump, and simply never returned. The runner eventually killed the host
/// and reported "Test host process crashed".
/// </para>
/// <para>
/// Every assertion here is bounded by a timeout rather than left to hang, so a
/// regression is a failed test with a name rather than a dead process.
/// </para>
/// </remarks>
public sealed class UiDispatcherTests
{
    /// <summary>Long enough for a real hop, short enough that a hang is a failure.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_dispatcher_that_has_shut_down_runs_the_work_rather_than_queueing_it()
    {
        var dispatcher = StartDispatcher(out var thread);

        dispatcher.InvokeShutdown();
        thread.Join(Bound);

        var ui = new UiDispatcher(dispatcher);
        var ran = false;

        // Before the fix this queued to a dispatcher whose message loop had
        // already returned, and the await never completed.
        await ui.InvokeAsync(() => ran = true).WaitAsync(Bound);

        Assert.True(ran);
    }

    [Fact]
    public void The_synchronous_path_survives_shutdown_too()
    {
        var dispatcher = StartDispatcher(out var thread);

        dispatcher.InvokeShutdown();
        thread.Join(Bound);

        var ran = false;

        // Dispatcher.Invoke on a dead dispatcher throws rather than hanging, but
        // throwing out of a settings save during shutdown is no better than
        // hanging in it.
        new UiDispatcher(dispatcher).Invoke(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public async Task A_live_dispatcher_is_still_marshalled_onto()
    {
        // The fix must not quietly turn every hop into an inline call: work
        // raised for the interface still has to reach the interface thread.
        var dispatcher = StartDispatcher(out var thread);

        try
        {
            var ui = new UiDispatcher(dispatcher);
            var ranOn = 0;

            await ui.InvokeAsync(() => ranOn = Environment.CurrentManagedThreadId).WaitAsync(Bound);

            Assert.Equal(thread.ManagedThreadId, ranOn);
            Assert.NotEqual(Environment.CurrentManagedThreadId, ranOn);
        }
        finally
        {
            dispatcher.InvokeShutdown();
            thread.Join(Bound);
        }
    }

    [Fact]
    public void The_test_host_never_binds_to_another_fixtures_dispatcher()
    {
        using var host = new TestAppHost();

        // A container built for a unit test must not marshal onto a interface
        // thread owned by a different fixture, whatever Application.Current
        // happens to be by the time it is built.
        Assert.IsType<ImmediateDispatcher>(host.Resolve<IUiDispatcher>());
    }

    [Fact]
    public async Task Saving_settings_completes_with_no_interface_running()
    {
        // The exact operation that wedged the run.
        using var host = new TestAppHost();
        var settings = host.Resolve<ISettingsService>();

        await settings
            .SaveAsync(settings.Current with { Aria2Enabled = true })
            .WaitAsync(Bound);

        Assert.True(settings.Current.Aria2Enabled);
    }

    [Fact]
    public async Task A_settings_subscriber_still_hears_about_the_change()
    {
        using var host = new TestAppHost();
        var settings = host.Resolve<ISettingsService>();

        AppSettings? heard = null;

        settings.SettingsChanged += (_, updated) => heard = updated;

        await settings
            .SaveAsync(settings.Current with { DiscoveryEnabled = true })
            .WaitAsync(Bound);

        // Running inline must not mean running never.
        Assert.NotNull(heard);
        Assert.True(heard.DiscoveryEnabled);
    }

    /// <summary>
    /// Starts a dispatcher on its own thread.
    /// </summary>
    /// <param name="thread">The thread it is running on.</param>
    /// <returns>The dispatcher, already pumping.</returns>
    private static Dispatcher StartDispatcher(out Thread thread)
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "dispatcher under test"
        };

        worker.Start();

        Assert.True(ready.Wait(Bound), "the dispatcher thread did not start");

        thread = worker;
        return dispatcher!;
    }
}
