using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using GameLauncher.Desktop;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Hosts a single WPF <see cref="Application"/> on a dedicated STA thread for
/// the lifetime of the test run.
/// </summary>
/// <remarks>
/// <para>
/// WPF imposes two constraints that a normal xunit test cannot satisfy: user
/// interface objects must be created on a single-threaded-apartment thread, and
/// only one <see cref="Application"/> may exist per process. xunit runs tests on
/// multi-threaded-apartment pool threads, so both constraints are violated by
/// default.
/// </para>
/// <para>
/// This host owns one STA thread with a running dispatcher and creates the real
/// <see cref="App"/> so that the application resource dictionaries declared in
/// <c>App.xaml</c> are loaded exactly as they are at runtime. Loading them any
/// other way — merging the same files by hand — would let the test pass while
/// the shipping application failed, because the list could drift.
/// </para>
/// <para>
/// <see cref="App.OnStartup"/> never runs: it is invoked by
/// <see cref="Application.Run()"/>, which this host deliberately does not call.
/// No generic host, database or window is created as a side effect.
/// </para>
/// </remarks>
public sealed class WpfTestHost : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private Dispatcher? _dispatcher;
    private Exception? _startupFailure;
    private bool _disposed;

    /// <summary>
    /// Starts the STA thread and waits for the application to be ready.
    /// </summary>
    /// <exception cref="InvalidOperationException">The host thread did not start in time.</exception>
    public WpfTestHost()
    {
        _thread = new Thread(RunApplication)
        {
            IsBackground = true,
            Name = "WPF test STA thread"
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("The WPF test host did not start within 30 seconds.");
        }

        if (_startupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(_startupFailure).Throw();
        }
    }

    /// <summary>
    /// Runs an action on the user interface thread and rethrows anything it
    /// throws on the caller's thread.
    /// </summary>
    /// <param name="action">The work to run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Exception? captured = null;

        _dispatcher!.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        if (captured is not null)
        {
            // Rethrown with its original stack intact so the failing XAML line is
            // still identifiable in the test output.
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    /// <summary>
    /// Runs asynchronous work on the user interface thread and awaits it from
    /// the caller's thread.
    /// </summary>
    /// <param name="work">The work to run.</param>
    /// <returns>A task that completes when the work does.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    /// <remarks>
    /// Separate from <see cref="Invoke"/> because that one blocks the caller
    /// while the dispatcher runs the delegate. Anything inside it that awaits
    /// with <c>ConfigureAwait(true)</c> — which is every view model in this
    /// application — would post its continuation to a dispatcher already
    /// occupied running the delegate, and deadlock. Awaiting from the caller's
    /// thread instead leaves the dispatcher free to pump those continuations.
    /// </remarks>
    public async Task InvokeAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Exception? captured = null;

        await _dispatcher!.InvokeAsync(async () =>
        {
            try
            {
                await work().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        }).Task.Unwrap().ConfigureAwait(false);

        if (captured is not null)
        {
            // Rethrown with its original stack intact so the failing XAML line is
            // still identifiable in the test output.
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    /// <summary>Entry point for the STA thread.</summary>
    private void RunApplication()
    {
        try
        {
            var application = new App();

            // Without this the Application defaults to OnLastWindowClose, and the
            // smoke tests show a window and close it again. Closing the last one
            // shuts the whole Application down, after which every later window
            // throws "The Application object is being shut down" and the
            // dispatcher stops — so the run does not merely fail, it hangs.
            //
            // This host owns the Application for the entire test run and disposes
            // it explicitly, so no window closing should ever end it.
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Loads App.xaml, which is what populates Application.Current.Resources
            // with the theme dictionaries the windows resolve against.
            application.InitializeComponent();

            _dispatcher = Dispatcher.CurrentDispatcher;
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            _ready.Set();
            return;
        }

        _ready.Set();
        Dispatcher.Run();
    }

    /// <summary>Shuts the dispatcher down and waits for the thread to finish.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
    }
}

/// <summary>
/// Shares one <see cref="WpfTestHost"/> across every test that needs a user
/// interface thread.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfTestHost>
{
    /// <summary>The collection name applied to WPF-dependent test classes.</summary>
    public const string Name = "WPF";
}
