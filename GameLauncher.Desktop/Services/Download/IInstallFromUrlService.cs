using GameLauncher.Desktop.Models;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// The stage an install is currently in.
/// </summary>
public enum InstallPhase
{
    /// <summary>Transferring the file.</summary>
    Downloading = 0,

    /// <summary>Checking the downloaded file against its checksum.</summary>
    Verifying = 1,

    /// <summary>Unpacking an archive.</summary>
    Extracting = 2,

    /// <summary>Looking for executables in what was unpacked.</summary>
    Detecting = 3,

    /// <summary>Finished.</summary>
    Completed = 4
}

/// <summary>
/// Progress of an install, across all of its stages.
/// </summary>
/// <param name="Phase">The stage in progress.</param>
/// <param name="Fraction">Completion within this stage, or <see langword="null"/> when unknown.</param>
/// <param name="Message">A line of text describing what is happening.</param>
public sealed record InstallProgress(InstallPhase Phase, double? Fraction, string Message);

/// <summary>
/// Describes a game to install from a direct download link.
/// </summary>
public sealed record InstallFromUrlRequest
{
    /// <summary>Direct URL of the file to download.</summary>
    public required Uri Url { get; init; }

    /// <summary>Expected hash as hex, or <see langword="null"/> to skip verification.</summary>
    public string? ExpectedChecksum { get; init; }

    /// <summary>
    /// Folder name to install into beneath the launcher's games directory, or
    /// <see langword="null"/> to derive one from the downloaded file name.
    /// </summary>
    public string? InstallFolderName { get; init; }

    /// <summary>Whether to delete the archive once it has been unpacked successfully.</summary>
    public bool DeleteArchiveAfterExtract { get; init; } = true;
}

/// <summary>
/// What an install produced, ready for the user to confirm.
/// </summary>
/// <param name="InstallDirectory">Folder the game was placed in.</param>
/// <param name="DownloadedFilePath">
/// The downloaded file, or <see langword="null"/> once an archive has been
/// deleted after unpacking.
/// </param>
/// <param name="WasArchive">Whether the download was unpacked.</param>
/// <param name="Candidates">Executables found, most promising first.</param>
/// <param name="Warning">A non-fatal problem worth showing, or <see langword="null"/>.</param>
public sealed record InstallPreparationResult(
    string InstallDirectory,
    string? DownloadedFilePath,
    bool WasArchive,
    IReadOnlyList<DiscoveredGame> Candidates,
    string? Warning);

/// <summary>
/// Installs a game from a direct download URL.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately generic: it downloads whatever URL it is given, unpacks it if it
/// is an archive, and looks for executables in the result. There is no
/// site-specific behaviour, no page parsing, no link discovery and no torrent or
/// magnet handling — the caller supplies a URL that already points directly at a
/// file.
/// </para>
/// <para>
/// The install stops short of adding anything to the library. It reports what it
/// found and the caller confirms, for the same reason folder scanning does:
/// automatic registration of whatever executable happened to sort first is a
/// guess presented as a decision.
/// </para>
/// </remarks>
public interface IInstallFromUrlService
{
    /// <summary>
    /// Downloads, verifies, unpacks and inspects a download, without registering
    /// anything.
    /// </summary>
    /// <param name="request">What to install.</param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>What was installed and which executables were found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The URL is not an absolute http or https address.</exception>
    /// <exception cref="InvalidOperationException">A checksum failed, or an archive could not be read.</exception>
    Task<InstallPreparationResult> PrepareAsync(
        InstallFromUrlRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
