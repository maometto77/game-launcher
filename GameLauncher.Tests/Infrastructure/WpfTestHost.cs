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

    /// <summary>Entry point for the STA thread.</summary>
    private void RunApplication()
    {
        try
        {
            var application = new App();

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
