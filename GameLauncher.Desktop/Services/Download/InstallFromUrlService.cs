using System.Text;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Library;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// Default <see cref="IInstallFromUrlService"/>.
/// </summary>
public sealed class InstallFromUrlService : IInstallFromUrlService
{
    private readonly IDownloadService _downloads;
    private readonly IArchiveExtractionService _archives;
    private readonly IGameScanService _scanner;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstallFromUrlService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="downloads">Transfers the file.</param>
    /// <param name="archives">Unpacks archives.</param>
    /// <param name="scanner">Finds executables in the installed folder.</param>
    /// <param name="paths">Supplies the download and install directories.</param>
    /// <param name="logger">Logger for install diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public InstallFromUrlService(
        IDownloadService downloads,
        IArchiveExtractionService archives,
        IGameScanService scanner,
        IAppPaths paths,
        ILogger<InstallFromUrlService> logger)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<InstallPreparationResult> PrepareAsync(
        InstallFromUrlRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ---- Download -------------------------------------------------------
        progress?.Report(new InstallProgress(InstallPhase.Downloading, null, "Connecting…"));

        var downloadProgress = new Progress<DownloadProgress>(update =>
            progress?.Report(new InstallProgress(
                InstallPhase.Downloading,
                update.Fraction,
                DescribeTransfer(update))));

        var download = await _downloads.DownloadAsync(
            new DownloadRequest
            {
                Url = request.Url,
                DestinationDirectory = _paths.DownloadDirectory,
                ExpectedChecksum = request.ExpectedChecksum,
                AllowResume = true
            },
            downloadProgress,
            cancellationToken).ConfigureAwait(false);

        if (download.ChecksumVerified)
        {
            progress?.Report(new InstallProgress(InstallPhase.Verifying, 1, "Checksum verified."));
        }

        var installFolderName = SanitiseFolderName(
            request.InstallFolderName ?? Path.GetFileNameWithoutExtension(download.FilePath));

        var installDirectory = Path.Combine(_paths.DefaultInstallDirectory, installFolderName);

        // ---- Unpack or place ------------------------------------------------
        string? warning = null;
        var wasArchive = _archives.IsSupportedArchive(download.FilePath);
        string? downloadedFilePath = download.FilePath;

        if (wasArchive)
        {
            progress?.Report(new InstallProgress(InstallPhase.Extracting, 0, "Unpacking…"));

            var extractionProgress = new Progress<ExtractionProgress>(update =>
                progress?.Report(new InstallProgress(
                    InstallPhase.Extracting, update.Fraction, $"Unpacking {update.CurrentEntry}")));

            var extraction = await _archives
                .ExtractAsync(download.FilePath, installDirectory, extractionProgress, cancellationToken)
                .ConfigureAwait(false);

            if (extraction.EntriesRejected > 0)
            {
                warning =
                    $"{extraction.EntriesRejected} file(s) in the archive tried to write outside the install " +
                    "folder and were skipped. Treat this download with suspicion.";
            }

            // Archives usually contain a single top-level folder. Descending into
            // it keeps the install directory pointing at the game rather than at a
            // wrapper folder, which matters because uninstall deletes this path.
            installDirectory = CollapseSingleRootFolder(installDirectory);

            if (request.DeleteArchiveAfterExtract)
            {
                TryDelete(download.FilePath);
                downloadedFilePath = null;
            }
        }
        else
        {
            // A bare executable or installer: move it into its own folder so the
            // library entry has an install directory of its own.
            Directory.CreateDirectory(installDirectory);

            var placed = Path.Combine(installDirectory, Path.GetFileName(download.FilePath));
            File.Move(download.FilePath, placed, overwrite: true);
            downloadedFilePath = placed;
        }

        // ---- Detect ---------------------------------------------------------
        progress?.Report(new InstallProgress(InstallPhase.Detecting, null, "Looking for the game executable…"));

        var candidates = await _scanner
            .ScanAsync(installDirectory, ScanOptions.Default, progress: null, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            warning ??= "No executable was found in the download. You may need to run an installer manually.";
        }

        progress?.Report(new InstallProgress(InstallPhase.Completed, 1, "Ready."));

        _logger.LogInformation(
            "Prepared install from {Url} into {Directory}; {Count} executable(s) found.",
            request.Url, installDirectory, candidates.Count);

        return new InstallPreparationResult(
            installDirectory,
            downloadedFilePath,
            wasArchive,
            candidates,
            warning);
    }

    /// <summary>
    /// If a folder contains exactly one subfolder and no files, returns that
    /// subfolder instead.
    /// </summary>
    /// <param name="directory">Folder to inspect.</param>
    /// <returns>The collapsed folder, or the original when it does not apply.</returns>
    /// <remarks>
    /// Archives are conventionally packed with one top-level directory. Without
    /// this the recorded install directory would be a wrapper containing a single
    /// folder, which looks wrong on the details page and makes "delete files"
    /// remove a level more than the user expects.
    /// </remarks>
    internal static string CollapseSingleRootFolder(string directory)
    {
        try
        {
            if (Directory.EnumerateFiles(directory).Any())
            {
                return directory;
            }

            var subdirectories = Directory.EnumerateDirectories(directory).Take(2).ToList();
            return subdirectories.Count == 1 ? subdirectories[0] : directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return directory;
        }
    }

    /// <summary>Builds a description of transfer progress for display.</summary>
    /// <param name="update">The progress update.</param>
    /// <returns>A line such as <c>48.2 MB of 1.4 GB — 6.1 MB/s, about 3 min left</c>.</returns>
    private static string DescribeTransfer(DownloadProgress update)
    {
        var builder = new StringBuilder();

        builder.Append(Helpers.ByteSizeConverter.Format(update.BytesReceived));

        if (update.TotalBytes is { } total)
        {
            builder.Append(" of ").Append(Helpers.ByteSizeConverter.Format(total));
        }

        if (update.BytesPerSecond > 0)
        {
            builder.Append("  ·  ")
                   .Append(Helpers.ByteSizeConverter.Format((long)update.BytesPerSecond))
                   .Append("/s");
        }

        if (update.EstimatedRemaining is { } remaining)
        {
            builder.Append("  ·  ").Append(DescribeRemaining(remaining)).Append(" left");
        }

        return builder.ToString();
    }

    /// <summary>Formats a remaining duration in coarse, honest terms.</summary>
    /// <param name="remaining">Estimated time left.</param>
    /// <returns>A short phrase such as <c>about 3 min</c>.</returns>
    private static string DescribeRemaining(TimeSpan remaining) => remaining switch
    {
        { TotalSeconds: < 10 } => "a few seconds",
        { TotalMinutes: < 1 } => $"{(int)remaining.TotalSeconds} sec",
        { TotalMinutes: < 60 } => $"about {(int)remaining.TotalMinutes} min",
        _ => $"about {(int)remaining.TotalHours} hr"
    };

    /// <summary>Reduces a name to something usable as a folder name.</summary>
    /// <param name="value">Candidate name.</param>
    /// <returns>A safe, non-empty folder name.</returns>
    private static string SanitiseFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "download";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value.Trim())
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var result = builder.ToString().Trim('.', ' ');

        if (result.Length > 100)
        {
            result = result[..100];
        }

        return string.IsNullOrWhiteSpace(result) ? "download" : result;
    }

    /// <summary>Deletes a file, ignoring failures.</summary>
    /// <param name="path">File to delete.</param>
    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the archive at {Path} after unpacking.", path);
        }
    }
}
