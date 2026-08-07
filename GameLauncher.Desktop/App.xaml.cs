using System.Windows;
using System.Windows.Threading;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Infrastructure.Logging;
using GameLauncher.Desktop.ViewModels;
using GameLauncher.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop;

/// <summary>
/// Application entry point. Owns the generic host that provides dependency
/// injection, configuration and logging for the whole client.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    /// <summary>
    /// Builds the host, starts it, and shows the shell window.
    /// </summary>
    /// <param name="e">Startup arguments supplied by WPF.</param>
    /// <remarks>
    /// <c>async void</c> is required here because it overrides a void member.
    /// Every path is wrapped, so no exception escapes into the WPF message loop.
    /// </remarks>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var options = StartupOptions.Parse(e.Args);

            // Parsed before the paths are built, because --state-dir decides where
            // every one of them points.
            var paths = options.StateDirectory is { Length: > 0 } stateDirectory
                ? new AppPaths(Path.GetFullPath(stateDirectory))
                : new AppPaths();

            paths.EnsureCreated();

            var builder = Host.CreateApplicationBuilder();

            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();
            builder.Logging.AddProvider(new FileLoggerProvider(paths.LogDirectory, LogLevel.Debug));
            builder.Logging.SetMinimumLevel(LogLevel.Debug);

            builder.Services.AddGameLauncher(paths, options);

            _host = builder.Build();
            _logger = _host.Services.GetRequiredService<ILogger<App>>();

            AttachGlobalExceptionHandlers();

            await _host.StartAsync().ConfigureAwait(true);
            _logger.LogInformation("GameLauncher starting. State directory: {Root}", paths.RootDirectory);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            var shell = _host.Services.GetRequiredService<MainWindowViewModel>();
            await shell.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Startup failed before the UI could report anything itself, so this
            // is the only chance to tell the user why the app is not opening.
            _logger?.LogCritical(ex, "Startup failed.");

            MessageBox.Show(
                $"GameLauncher could not start.\n\n{ex.Message}",
                "GameLauncher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    /// <summary>
    /// Stops the host and flushes logs.
    /// </summary>
    /// <param name="e">Exit arguments supplied by WPF.</param>
    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                _logger?.LogInformation("GameLauncher shutting down with code {ExitCode}.", e.ApplicationExitCode);

                // Bounded so a wedged background service cannot hang the exit.
                await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                _host.Dispose();
                _host = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Shutdown did not complete cleanly.");
        }
        finally
        {
            base.OnExit(e);
        }
    }

    /// <summary>
    /// Subscribes to the three channels through which an unhandled exception can
    /// surface, so none of them terminates the process silently.
    /// </summary>
    private void AttachGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Handles an exception raised on the UI thread.
    /// </summary>
    /// <param name="sender">The dispatcher raising the event.</param>
    /// <param name="e">Event data carrying the exception.</param>
    /// <remarks>
    /// Marked handled so a fault in one interaction does not take the whole app
    /// down; the user is told, and the detail goes to the log.
    /// </remarks>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled exception on the UI thread.");

        MessageBox.Show(
            $"Something went wrong.\n\n{e.Exception.Message}",
            "GameLauncher",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    /// <summary>
    /// Logs an exception that reached the AppDomain, which is always fatal.
    /// </summary>
    /// <param name="sender">The AppDomain raising the event.</param>
    /// <param name="e">Event data carrying the exception object.</param>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // The process is going down regardless; the only useful action left is
        // to get the reason on disk before it does.
        _logger?.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception. Terminating: {IsTerminating}", e.IsTerminating);
    }

    /// <summary>
    /// Logs and observes an exception from a faulted task nobody awaited.
    /// </summary>
    /// <param name="sender">The task scheduler raising the event.</param>
    /// <param name="e">Event data carrying the aggregate exception.</param>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");

        // Observing it prevents the default escalation to a process-level crash.
        e.SetObserved();
    }
}
