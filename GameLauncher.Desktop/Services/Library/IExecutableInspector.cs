using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// The outcome of validating an executable before launching it.
/// </summary>
/// <param name="IsLaunchable">Whether the launcher is willing to start it.</param>
/// <param name="Problem">
/// A user-facing description of what is wrong, or <see langword="null"/> when
/// the executable is fine.
/// </param>
/// <param name="IsWarningOnly">
/// When <see langword="true"/>, the executable can still be launched but
/// something about it is worth telling the user first.
/// </param>
public sealed record ExecutableValidation(bool IsLaunchable, string? Problem, bool IsWarningOnly)
{
    /// <summary>A validation result indicating no problems were found.</summary>
    public static ExecutableValidation Ok { get; } = new(true, null, false);

    /// <summary>Creates a blocking failure.</summary>
    /// <param name="problem">User-facing reason the executable cannot be launched.</param>
    /// <returns>A failed validation.</returns>
    public static ExecutableValidation Fail(string problem) => new(false, problem, false);

    /// <summary>Creates a non-blocking warning.</summary>
    /// <param name="problem">User-facing description of the concern.</param>
    /// <returns>A validation that permits launching but carries a message.</returns>
    public static ExecutableValidation Warn(string problem) => new(true, problem, true);
}

/// <summary>
/// Reads metadata out of executables and validates them.
/// </summary>
public interface IExecutableInspector
{
    /// <summary>
    /// Reads everything that can be learned about an executable without running it.
    /// </summary>
    /// <param name="executablePath">Absolute path to the file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The metadata gathered. Fields that could not be read are null, and
    /// <see cref="ExecutableInfo.IsValidExecutable"/> is false when the file is
    /// not a portable executable.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="executablePath"/> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    Task<ExecutableInfo> InspectAsync(string executablePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides whether an executable is safe and sensible to launch.
    /// </summary>
    /// <param name="executablePath">Absolute path to the file.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The validation outcome.</returns>
    Task<ExecutableValidation> ValidateAsync(string executablePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a file name looks like an installer, uninstaller,
    /// redistributable or crash handler rather than a game.
    /// </summary>
    /// <param name="fileName">File name, with or without a directory.</param>
    /// <returns><see langword="true"/> when the name matches a known non-game pattern.</returns>
    bool IsKnownNonGame(string fileName);
}
