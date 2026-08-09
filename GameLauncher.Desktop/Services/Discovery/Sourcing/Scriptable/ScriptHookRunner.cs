using System.Diagnostics;
using System.IO;
using GameLauncher.Desktop.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// Runs a manifest's transform program over a payload.
/// </summary>
public interface IScriptHookRunner
{
    /// <summary>
    /// Passes a payload through an external program.
    /// </summary>
    /// <param name="transform">The program to run.</param>
    /// <param name="payload">Text written to its standard input.</param>
    /// <param name="workingDirectory">Directory the program runs in.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the program wrote to standard output.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The program failed, timed out or produced nothing.</exception>
    Task<string> RunAsync(
        FeedTransform transform,
        string payload,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IScriptHookRunner"/>: a child process, a pipe each way.
/// </summary>
/// <remarks>
/// <para>
/// This is what a "script hook" is here. The launcher embeds no scripting
/// engine; it runs a program the user named in a file the user wrote, hands it
/// the payload on standard input, and reads JSON back from standard output. Lua,
/// JavaScript, Python and compiled binaries all satisfy that contract without
/// this project taking a dependency on any of them.
/// </para>
/// <para>
/// The choice is not only about dependencies. An in-process interpreter runs a
/// manifest's code with the launcher's own file handles, database connection and
/// user token; a child process runs it as a separate program the operating
/// system can account for and the user can see in a task list. For code arriving
/// from outside the application, the weaker coupling is the point.
/// </para>
/// <para>
/// The program is resolved against the adapter directory, so a manifest cannot
/// name an arbitrary path elsewhere on the machine and have it run. A hook is
/// something the user put in the folder, alongside the manifest that calls it.
/// </para>
/// </remarks>
public sealed class ScriptHookRunner : IScriptHookRunner
{
    /// <summary>Largest output accepted from a hook.</summary>
    /// <remarks>
    /// A hook that writes without end would otherwise be read without end. Feed
    /// payloads are kilobytes; this is four megabytes, which is generous for the
    /// job and finite, which is the property that matters.
    /// </remarks>
    private const int MaxOutputBytes = 4 * 1024 * 1024;

    private readonly IAppPaths _paths;
    private readonly ILogger<ScriptHookRunner> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="paths">Supplies the adapter directory a hook must live in.</param>
    /// <param name="logger">Logger for hook diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ScriptHookRunner(IAppPaths paths, ILogger<ScriptHookRunner> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> RunAsync(
        FeedTransform transform,
        string payload,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var start = new ProcessStartInfo
        {
            FileName = transform.Command,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in transform.Args)
        {
            // Added one at a time so the runtime quotes each; building a command
            // line by hand is how a path with a space in it becomes two broken
            // arguments.
            start.ArgumentList.Add(ResolveArgument(argument));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(transform.TimeoutSeconds, 1, 300)));

        using var process = new Process { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The transform '{transform.Command}' could not be started: {ex.Message}", ex);
        }

        try
        {
            // Written and read concurrently. A hook that reads its whole input
            // before writing anything would deadlock against a parent doing the
            // reverse, and both are reasonable ways to write one.
            var write = WriteInputAsync(process, payload, timeout.Token);
            var output = ReadAsync(process.StandardOutput, timeout.Token);
            var error = ReadAsync(process.StandardError, timeout.Token);

            await Task.WhenAll(write, output, error).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (error.Result is { Length: > 0 } diagnostics)
            {
                // Reported at debug even on success: a hook is entitled to write
                // progress to standard error, and treating that as a failure
                // would make the well-behaved case look broken.
                _logger.LogDebug("Transform '{Command}' wrote to standard error: {Message}",
                    transform.Command, diagnostics.Trim());
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The transform '{transform.Command}' exited with code {process.ExitCode}. " +
                    (error.Result.Length > 0 ? error.Result.Trim() : "It wrote nothing to standard error."));
            }

            return output.Result.Length > 0
                ? output.Result
                : throw new InvalidOperationException(
                    $"The transform '{transform.Command}' produced no output.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);

            throw new InvalidOperationException(
                $"The transform '{transform.Command}' did not finish within {transform.TimeoutSeconds}s.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>
    /// Resolves an argument that names a file in the adapter directory.
    /// </summary>
    /// <param name="argument">The argument as the manifest wrote it.</param>
    /// <returns>An absolute path when it names a script beside the manifest, otherwise the argument.</returns>
    /// <remarks>
    /// So a manifest can say <c>args: [parse.js]</c> and mean the file next to
    /// it, without the user having to write an absolute path that stops working
    /// the moment the folder moves. Anything that is not a file in that folder
    /// passes through untouched — most arguments are flags, not paths.
    /// </remarks>
    private string ResolveArgument(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument) || Path.IsPathRooted(argument))
        {
            return argument;
        }

        var candidate = Path.Combine(_paths.AdapterDirectory, argument);

        return File.Exists(candidate) ? candidate : argument;
    }

    /// <summary>Writes the payload to the process and closes its input.</summary>
    /// <param name="process">The running process.</param>
    /// <param name="payload">What to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the input is closed.</returns>
    private static async Task WriteInputAsync(Process process, string payload, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A hook that does not read its input is allowed. The pipe breaking
            // is how that arrives here, and it is not an error.
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    /// <summary>Reads a stream to its end, up to the output limit.</summary>
    /// <param name="reader">The stream to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What was read.</returns>
    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var text = new System.Text.StringBuilder();

        while (text.Length < MaxOutputBytes)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    /// <summary>Ends a process that has outstayed its welcome.</summary>
    /// <param name="process">The process to end.</param>
    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or
                                       System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to end. Either way there is nothing left
            // to do and throwing here would replace the real failure.
            _logger.LogDebug(ex, "Could not end the transform process.");
        }
    }
}
