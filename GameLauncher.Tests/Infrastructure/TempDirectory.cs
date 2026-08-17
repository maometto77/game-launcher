namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// A temporary directory removed when the test finishes.
/// </summary>
/// <remarks>
/// Shared rather than nested in one test class, because several suites now need
/// a real folder on a real disk: a download writing part files, a probe listing
/// account directories, a watcher reading an emulator's layout. Each gets its
/// own GUID-named folder, so tests running in parallel cannot see each other's.
/// </remarks>
public sealed class TempDirectory : IDisposable
{
    /// <summary>Creates and returns a fresh directory under the system temp path.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    /// <summary>Gets the directory's full path.</summary>
    public string Path { get; }

    /// <summary>Removes the directory and everything under it.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }
}
