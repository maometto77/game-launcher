namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Finds a Python interpreter for the tests that exercise a shipped script.
/// </summary>
/// <remarks>
/// The hook contract is a pipe, so the launcher needs no interpreter and the
/// test suite must not require one either. The tests that would run a script
/// stand down when there is nothing to run them with, rather than failing on a
/// machine that is configured exactly as intended.
/// </remarks>
internal static class PythonInterpreter
{
    private static readonly Lazy<string?> Found = new(Probe, isThreadSafe: true);

    /// <summary>Gets the command to run, or <see langword="null"/> when there is none.</summary>
    /// <remarks>
    /// Probed once. Starting three processes per test to answer the same
    /// question is the sort of thing that makes a suite slow for no reason.
    /// </remarks>
    public static string? Command => Found.Value;

    /// <summary>Tries the spellings an interpreter goes by.</summary>
    /// <returns>The first that answers, or <see langword="null"/>.</returns>
    private static string? Probe()
    {
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (probe is null)
                {
                    continue;
                }

                probe.WaitForExit(10_000);

                if (probe.HasExited && probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Not on PATH. Try the next spelling.
            }
        }

        return null;
    }
}
