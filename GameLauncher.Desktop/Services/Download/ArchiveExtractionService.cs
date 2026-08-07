using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// Default <see cref="IArchiveExtractionService"/>, built on SharpCompress.
/// </summary>
/// <remarks>
/// Entries are written by hand rather than through SharpCompress's
/// <c>WriteToDirectory</c> helper, so that every destination path is validated
/// before anything is opened for writing. See <see cref="TryResolveEntryPath"/>
/// for why that matters.
/// </remarks>
public sealed class ArchiveExtractionService : IArchiveExtractionService
{
    private const int BufferSize = 128 * 1024;

    /// <summary>Smallest gap between progress reports.</summary>
    /// <remarks>
    /// A few updates a second is as much as anyone can read, and an archive of
    /// several thousand small files would otherwise spend more time marshalling
    /// progress onto the interface thread than unpacking.
    /// </remarks>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Extensions treated as archives.
    /// </summary>
    /// <remarks>
    /// Used only as a cheap pre-check for deciding whether a download is worth
    /// trying to open. The authority on whether a file is really an archive is
    /// SharpCompress, which sniffs the content.
    /// </remarks>
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".lz"
    };

    private readonly ILogger<ArchiveExtractionService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="logger">Logger for extraction diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public ArchiveExtractionService(ILogger<ArchiveExtractionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsSupportedArchive(string path) =>
        !string.IsNullOrWhiteSpace(path) && ArchiveExtensions.Contains(Path.GetExtension(path));

    /// <inheritdoc />
    public async Task<ExtractionResult> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The archive does not exist.", archivePath);
        }

        return await Task.Run(
            () => ExtractCore(archivePath, destinationDirectory, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the extraction synchronously on a background thread.
    /// </summary>
    /// <param name="archivePath">Archive to read.</param>
    /// <param name="destinationDirectory">Folder to write into.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>Details of what was written.</returns>
    private ExtractionResult ExtractCore(
        string archivePath,
        string destinationDirectory,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        IArchive archive;
        try
        {
            archive = OpenArchive(archivePath);
        }
        catch (Exception ex)
        {
            // SharpCompress throws a variety of format-specific exceptions; the
            // caller only needs to know the file was not usable as an archive.
            throw new InvalidOperationException(
                $"'{Path.GetFileName(archivePath)}' could not be opened as an archive. " +
                "It may be corrupt, encrypted, or in an unsupported format.", ex);
        }

        using (archive)
        {
            // Reading the entry list is a header operation and costs nothing; it
            // is opening an entry's *content* by random access that is expensive.
            var totalEntries = archive.Entries.Count(entry => !entry.IsDirectory);

            var extracted = 0;
            var rejected = 0;
            long totalBytes = 0;

            var lastReport = Stopwatch.StartNew();

            // Writes one entry. Shared by both iteration strategies below so the
            // path validation guarding the destination has exactly one
            // implementation.
            void Write(string? key, long size, Func<Stream> openEntry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Validated before the entry is opened, so a refused path is never
                // decoded and never reaches a FileStream.
                if (!TryResolveEntryPath(destinationRoot, key, out var targetPath))
                {
                    rejected++;
                    return;
                }

                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var source = openEntry())
                using (var destination = new FileStream(
                           targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                {
                    source.CopyTo(destination, BufferSize);
                }

                extracted++;
                totalBytes += Math.Max(0, size);

                // Throttled for the same reason the download reports are: an
                // archive of a few thousand small files would otherwise post a
                // few thousand updates to the dispatcher, and redrawing the
                // progress line would cost more than unpacking the file.
                if (lastReport.Elapsed >= ProgressInterval)
                {
                    lastReport.Restart();
                    progress?.Report(new ExtractionProgress(extracted, totalEntries, key ?? string.Empty));
                }
            }

            if (archive.IsSolid)
            {
                // A solid archive compresses every file into one continuous
                // stream, so opening an entry directly makes the decoder run from
                // the start of that stream to reach it — one full decode per
                // entry, which is quadratic and ruinous on a real game archive.
                // Measured on a 650 MB, 2192-entry 7z: a single late entry took
                // 875 ms by random access, while one forward pass over all 2192
                // took 59 seconds in total.
                //
                // A reader decodes the stream once and hands over each entry as it
                // passes. Skipping an entry is simply not opening it;
                // MoveToNextEntry steps over the remaining bytes.
                using var reader = archive.ExtractAllEntries();

                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory)
                    {
                        continue;
                    }

                    Write(reader.Entry.Key, reader.Entry.Size, reader.OpenEntryStream);
                }
            }
            else
            {
                // Zip and other non-solid formats compress each entry
                // independently, so seeking straight to one costs nothing extra.
                // SharpCompress refuses ExtractAllEntries here for exactly that
                // reason, and random access keeps rejected entries from being
                // decoded at all.
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                {
                    Write(entry.Key, entry.Size, entry.OpenEntryStream);
                }
            }

            // Always report the finished state, however the throttle fell.
            progress?.Report(new ExtractionProgress(extracted, totalEntries, string.Empty));

            if (rejected > 0)
            {
                // Surfaced rather than silent: an archive containing traversal
                // entries is either malicious or badly broken, and the user should
                // know their extraction was incomplete.
                _logger.LogWarning(
                    "Refused {Count} entr{Suffix} in {Archive} whose paths escaped the destination folder.",
                    rejected, rejected == 1 ? "y" : "ies", Path.GetFileName(archivePath));
            }

            _logger.LogInformation(
                "Extracted {Count} entries ({Bytes} bytes) from {Archive} into {Destination}.",
                extracted, totalBytes, Path.GetFileName(archivePath), destinationRoot);

            return new ExtractionResult(destinationRoot, extracted, rejected, totalBytes);
        }
    }

    /// <summary>Opens an archive for reading, letting SharpCompress detect the format.</summary>
    /// <param name="archivePath">Path to the archive.</param>
    /// <returns>The opened archive, which the caller disposes.</returns>
    /// <remarks>
    /// The format is determined by sniffing content, not by file extension, so a
    /// <c>.zip</c> that is really a 7z still opens correctly.
    /// </remarks>
    private static IArchive OpenArchive(string archivePath) =>
        ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { LeaveStreamOpen = false });

    /// <summary>
    /// Resolves an archive entry to an absolute path, refusing anything that
    /// escapes the destination folder.
    /// </summary>
    /// <param name="destinationRoot">Absolute, normalised destination folder.</param>
    /// <param name="entryKey">The entry's path as recorded in the archive.</param>
    /// <param name="targetPath">Receives the resolved path when the entry is safe.</param>
    /// <returns><see langword="true"/> when the entry may be written.</returns>
    /// <remarks>
    /// This is the defence against path traversal, commonly called "zip slip".
    /// Archive entry names are attacker-controlled: an entry called
    /// <c>..\..\..\Startup\evil.exe</c> would, if joined naively, write outside
    /// the destination and into a folder Windows executes at login. Resolving the
    /// combined path and requiring it to remain under the destination is what
    /// makes that impossible.
    /// </remarks>
    internal static bool TryResolveEntryPath(
        string destinationRoot,
        string? entryKey,
        out string targetPath)
    {
        targetPath = string.Empty;

        if (string.IsNullOrWhiteSpace(entryKey))
        {
            return false;
        }

        // Archives use forward slashes; Windows paths may arrive either way.
        var relative = entryKey.Replace('/', Path.DirectorySeparatorChar)
                               .Replace('\\', Path.DirectorySeparatorChar)
                               .TrimStart(Path.DirectorySeparatorChar);

        if (relative.Length == 0)
        {
            return false;
        }

        // An absolute or rooted entry ("C:\..." or "\Windows\...") is never valid.
        if (Path.IsPathRooted(relative))
        {
            return false;
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(destinationRoot, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = destinationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        targetPath = combined;
        return true;
    }
}
