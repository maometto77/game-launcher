namespace GameLauncher.Desktop.Models;

/// <summary>
/// The processor architecture an executable was built for.
/// </summary>
public enum ExecutableArchitecture
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>32-bit x86.</summary>
    X86 = 1,

    /// <summary>64-bit x86.</summary>
    X64 = 2,

    /// <summary>64-bit ARM.</summary>
    Arm64 = 3
}

/// <summary>
/// The Windows subsystem an executable requests.
/// </summary>
/// <remarks>
/// A useful signal when guessing whether a discovered executable is a game:
/// games are almost always <see cref="WindowsGui"/>, while build tools, servers
/// and helper utilities shipped alongside them are frequently console
/// applications.
/// </remarks>
public enum ExecutableSubsystem
{
    /// <summary>Could not be determined.</summary>
    Unknown = 0,

    /// <summary>A windowed application.</summary>
    WindowsGui = 2,

    /// <summary>A console application.</summary>
    WindowsConsole = 3
}

/// <summary>
/// Everything the launcher can learn about an executable without running it.
/// </summary>
/// <param name="Path">Absolute path to the file.</param>
/// <param name="FileName">File name including extension.</param>
/// <param name="SuggestedTitle">Best available display name for the game.</param>
/// <param name="ProductName">Product name from the version resource, if present.</param>
/// <param name="FileDescription">File description from the version resource, if present.</param>
/// <param name="CompanyName">Publisher from the version resource, if present.</param>
/// <param name="FileVersion">File version from the version resource, if present.</param>
/// <param name="FileSizeBytes">Size of the executable itself, not its install folder.</param>
/// <param name="Architecture">Processor architecture from the PE header.</param>
/// <param name="Subsystem">Windows subsystem from the PE header.</param>
/// <param name="IsValidExecutable">
/// Whether the file is a well-formed portable executable. False for a file that
/// merely ends in <c>.exe</c>.
/// </param>
public sealed record ExecutableInfo(
    string Path,
    string FileName,
    string SuggestedTitle,
    string? ProductName,
    string? FileDescription,
    string? CompanyName,
    string? FileVersion,
    long FileSizeBytes,
    ExecutableArchitecture Architecture,
    ExecutableSubsystem Subsystem,
    bool IsValidExecutable)
{
    /// <summary>Gets the directory containing the executable.</summary>
    public string? Directory => System.IO.Path.GetDirectoryName(Path);

    /// <summary>Gets a short description of architecture and subsystem for display.</summary>
    public string PlatformSummary => Architecture switch
    {
        ExecutableArchitecture.X86 => "32-bit",
        ExecutableArchitecture.X64 => "64-bit",
        ExecutableArchitecture.Arm64 => "ARM64",
        _ => "Unknown architecture"
    };
}
