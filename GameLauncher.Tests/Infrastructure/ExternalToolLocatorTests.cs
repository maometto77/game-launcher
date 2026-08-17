using GameLauncher.Desktop.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Covers how an external program is found: where it is looked for, in what
/// order, and what counts as finding one.
/// </summary>
/// <remarks>
/// The search order is the whole contract. A bundled copy has to win over one on
/// PATH so an installer's version is what runs, and an explicitly configured
/// path has to win over both because someone who named a path has already
/// decided.
/// </remarks>
public sealed class ExternalToolLocatorTests
{
    [Fact]
    public void The_configured_path_is_searched_before_anything_else()
    {
        using var host = new TestAppHost();

        var paths = host.Resolve<IExternalToolLocator>()
            .GetSearchPaths("aria2c", @"D:\my-tools\aria2c.exe");

        Assert.Equal(@"D:\my-tools\aria2c.exe", paths[0]);
    }

    [Fact]
    public void A_bundled_copy_is_preferred_over_one_on_the_path()
    {
        using var host = new TestAppHost();

        var paths = host.Resolve<IExternalToolLocator>().GetSearchPaths("aria2c");

        var beside = IndexOf(paths, Path.Combine(AppContext.BaseDirectory, "aria2c.exe"));

        var bundled = IndexOf(
            paths, Path.Combine(AppContext.BaseDirectory, ExternalToolLocator.BundledToolsFolder, "aria2c.exe"));

        var onPath = IndexOf(paths, "aria2c.exe");

        Assert.True(beside >= 0 && bundled >= 0 && onPath >= 0);

        // The bare name is last: it is the only candidate the launcher does not
        // control, so an installer's copy must be found before it.
        Assert.True(beside < onPath);
        Assert.True(bundled < onPath);
        Assert.Equal(paths.Count - 1, onPath);
    }

    /// <summary>Finds where a candidate sits in the search order.</summary>
    /// <param name="paths">The search order.</param>
    /// <param name="candidate">The path to look for.</param>
    /// <returns>Its position, or -1.</returns>
    private static int IndexOf(IReadOnlyList<string> paths, string candidate)
    {
        for (var index = 0; index < paths.Count; index++)
        {
            if (paths[index].Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    [Fact]
    public void The_per_user_tools_folder_is_searched_too()
    {
        // So a tool can be added without write access to Program Files.
        using var host = new TestAppHost();

        var expected = Path.Combine(
            host.Resolve<IAppPaths>().RootDirectory, ExternalToolLocator.BundledToolsFolder, "aria2c.exe");

        Assert.Contains(
            host.Resolve<IExternalToolLocator>().GetSearchPaths("aria2c"),
            path => path.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_program_that_is_not_there_is_reported_as_missing()
    {
        using var host = new TestAppHost();

        Assert.Null(await host.Resolve<IExternalToolLocator>()
            .LocateAsync("definitely-not-a-real-program-anywhere"));
    }

    [Fact]
    public async Task A_file_that_exists_but_will_not_run_is_not_accepted()
    {
        // The reason the probe exists. A zero-byte placeholder, a copy for the
        // wrong architecture, or something quarantined by security software all
        // pass an existence check and fail to start — and finding that out
        // during a download would be far worse than finding it out here.
        using var temp = new TempDirectory();
        using var host = new TestAppHost();

        var impostor = Path.Combine(temp.Path, "aria2c.exe");

        await File.WriteAllBytesAsync(impostor, new byte[64]);

        Assert.True(File.Exists(impostor));
        Assert.Null(await host.Resolve<IExternalToolLocator>().LocateAsync("aria2c", impostor));
    }

    [Fact]
    public async Task A_program_that_answers_the_probe_is_accepted()
    {
        using var temp = new TempDirectory();
        using var host = new TestAppHost();

        // Stands in for a bundled tool: it answers --version with success, which
        // is exactly what the locator asks of a candidate.
        var stub = Path.Combine(temp.Path, "pretend-tool.cmd");

        await File.WriteAllTextAsync(stub, "@echo off\r\nexit /b 0\r\n");

        Assert.Equal(stub, await host.Resolve<IExternalToolLocator>().LocateAsync("pretend-tool", stub));
    }

    [Fact]
    public async Task A_failing_probe_rules_the_candidate_out()
    {
        using var temp = new TempDirectory();
        using var host = new TestAppHost();

        var stub = Path.Combine(temp.Path, "broken-tool.cmd");

        await File.WriteAllTextAsync(stub, "@echo off\r\nexit /b 1\r\n");

        Assert.Null(await host.Resolve<IExternalToolLocator>().LocateAsync("broken-tool", stub));
    }

    [Fact]
    public async Task The_answer_is_remembered_rather_than_re_probed()
    {
        // Probing starts a process. Doing that on every download to re-learn
        // something that has not changed would be pure waste.
        using var temp = new TempDirectory();
        using var host = new TestAppHost();

        var stub = Path.Combine(temp.Path, "cached-tool.cmd");

        await File.WriteAllTextAsync(stub, "@echo off\r\nexit /b 0\r\n");

        var locator = host.Resolve<IExternalToolLocator>();

        Assert.Equal(stub, await locator.LocateAsync("cached-tool", stub));

        // Deleted after the first answer. A locator that re-probed would now
        // report it missing.
        File.Delete(stub);

        Assert.Equal(stub, await locator.LocateAsync("cached-tool", stub));
    }

    [Fact]
    public void An_executable_extension_is_only_added_to_the_bare_name()
    {
        using var host = new TestAppHost();

        var paths = host.Resolve<IExternalToolLocator>().GetSearchPaths("aria2c", @"D:\tools\my-aria2");

        // A configured path is used exactly as written: someone who pointed at a
        // file without an extension meant that file.
        Assert.Equal(@"D:\tools\my-aria2", paths[0]);
        Assert.Equal("aria2c.exe", paths[^1]);
    }
}
