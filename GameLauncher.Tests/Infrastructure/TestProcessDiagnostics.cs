using System.Runtime.CompilerServices;
using System.Text;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Records the faults that end a test host without failing a test.
/// </summary>
/// <remarks>
/// <para>
/// A test that throws is reported by the runner with a stack and a name. A
/// background thread that throws is not: the process dies, the runner says
/// "Test host process crashed", and every result in flight is lost — including
/// the identity of whatever caused it. That failure mode is the reason this
/// exists.
/// </para>
/// <para>
/// Writes to a file rather than the console because the console buffer goes with
/// the process. The file is opened, appended and closed on each fault, so a
/// crash immediately afterwards still leaves the record on disk.
/// </para>
/// </remarks>
public static class TestProcessDiagnostics
{
    private static readonly object Gate = new();

    /// <summary>Gets the file faults are recorded to.</summary>
    public static string LogPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "test-process-faults.log");

    /// <summary>
    /// Subscribes to the process-wide fault channels.
    /// </summary>
    /// <remarks>
    /// A module initializer so it is in place before the first test runs, and
    /// before any fixture has had a chance to start a background thread.
    /// </remarks>
    [ModuleInitializer]
    internal static void Attach()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Record("AppDomain.UnhandledException", e.ExceptionObject as Exception, $"terminating={e.IsTerminating}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("TaskScheduler.UnobservedTaskException", e.Exception);

            // Observed so it cannot escalate, and so the record above is the
            // whole story rather than one of two competing explanations.
            e.SetObserved();
        };
    }

    /// <summary>
    /// Appends one fault to the log.
    /// </summary>
    /// <param name="channel">Which channel reported it.</param>
    /// <param name="exception">The fault, when there is one.</param>
    /// <param name="note">Anything else worth recording.</param>
    public static void Record(string channel, Exception? exception, string? note = null)
    {
        var entry = new StringBuilder()
            .Append('[').Append(DateTimeOffset.Now.ToString("HH:mm:ss.fff")).Append("] ")
            .Append(channel)
            .Append(note is null ? string.Empty : $" ({note})")
            .AppendLine()
            .AppendLine(exception?.ToString() ?? "(no exception)")
            .AppendLine(new string('-', 72))
            .ToString();

        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, entry);
            }
        }
        catch (Exception)
        {
            // A diagnostic that throws while recording a crash would replace the
            // information it exists to preserve.
        }
    }
}
