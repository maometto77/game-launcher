using System.Diagnostics;
using System.IO;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Download;

/// <summary>
/// Covers the register that lets one launcher clean up after the last one.
/// </summary>
/// <remarks>
/// The incident behind this: a launcher killed to free a locked file during a
/// rebuild left an <c>aria2c</c> downloading at full speed for the best part of
/// an hour, invisible in the Downloads table, while a later launcher reported
/// the same download as doing nothing. The job object stops new ones being
/// created; this stops the ones already out there.
/// </remarks>
public sealed class DownloadHelperRegistryTests
{
    [Fact]
    public void A_recorded_helper_still_running_is_stopped_by_the_next_launcher()
    {
        using var directory = new TempDirectory();

        // A stand-in for the stranded helper. What matters is that it is a live
        // process the register names, not which program it is.
        using var stray = Spawn();

        Registry(directory).Register(stray);

        // A different instance, exactly as the next launch would be.
        var stopped = Registry(directory).Sweep();

        Assert.Equal(1, stopped);
        Assert.True(stray.WaitForExit(10_000), "the sweep did not stop the recorded helper");
    }

    [Fact]
    public void A_helper_that_was_forgotten_is_left_alone()
    {
        using var directory = new TempDirectory();
        using var stray = Spawn();

        var registry = Registry(directory);

        registry.Register(stray);

        // What a clean exit does: the transport forgets each helper as it ends.
        registry.Forget(stray.Id);

        Assert.Equal(0, Registry(directory).Sweep());
        Assert.False(stray.HasExited);

        stray.Kill(entireProcessTree: true);
        stray.WaitForExit(10_000);
    }

    [Fact]
    public void A_reused_process_id_is_not_mistaken_for_the_helper()
    {
        // Windows reuses process ids. Without the start time in the register, a
        // sweep could kill whatever inherited the id — which would be far worse
        // than leaving a stray download running.
        using var directory = new TempDirectory();
        using var innocent = Spawn();

        var paths = new AppPaths(directory.Path);
        paths.EnsureCreated();

        // The right id, deliberately the wrong start time.
        File.WriteAllLines(
            Path.Combine(paths.RootDirectory, "download-helpers.txt"),
            [$"{innocent.Id}\t{innocent.StartTime.Ticks + 1}"]);

        Assert.Equal(0, Registry(directory).Sweep());
        Assert.False(innocent.HasExited);

        innocent.Kill(entireProcessTree: true);
        innocent.WaitForExit(10_000);
    }

    [Fact]
    public void A_sweep_with_nothing_recorded_does_nothing()
    {
        using var directory = new TempDirectory();

        Assert.Equal(0, Registry(directory).Sweep());
    }

    [Fact]
    public void A_dead_helper_is_swept_without_complaint()
    {
        // The ordinary case after a crash: the launcher died, and so did its
        // helper. The entry is stale rather than actionable.
        using var directory = new TempDirectory();

        var stray = Spawn();
        var registry = Registry(directory);

        registry.Register(stray);

        stray.Kill(entireProcessTree: true);
        stray.WaitForExit(10_000);
        stray.Dispose();

        Assert.Equal(0, Registry(directory).Sweep());
    }

    /// <summary>Builds a register over a directory.</summary>
    /// <param name="directory">The state directory to keep it in.</param>
    /// <returns>The register.</returns>
    /// <remarks>
    /// A fresh instance each time on purpose: the register's whole job is to carry
    /// state between launcher instances through the file, so a test that reused one
    /// object would prove nothing about that.
    /// </remarks>
    private static DownloadHelperRegistry Registry(TempDirectory directory)
    {
        var paths = new AppPaths(directory.Path);
        paths.EnsureCreated();

        // "cmd" stands in for aria2c: the register verifies the process name so a
        // sweep can never kill something that merely inherited the id, and that
        // guard is only observable if the name is substitutable.
        return new DownloadHelperRegistry(
            paths, NullLogger<DownloadHelperRegistry>.Instance, helperName: "cmd");
    }

    /// <summary>Starts a process that waits until it is killed.</summary>
    /// <returns>The running process.</returns>
    private static Process Spawn()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c pause",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);

        return process!;
    }
}
