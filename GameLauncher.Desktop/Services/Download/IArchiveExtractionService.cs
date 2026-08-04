namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// Progress of a running extraction.
/// </summary>
/// <param name="EntriesExtracted">Entries written so far.</param>
/// <param name="TotalEntries">Total entries in the archive.</param>
/// <param name="CurrentEntry">Name of the entry being written.</param>
public sealed record ExtractionProgress(int EntriesExtracted, int TotalEntries, string CurrentEntry)
{
    /// <summary>Gets completion as a fraction between zero and one.</summary>
    public double Fraction =>
        TotalEntries > 0 ? Math.Clamp((double)EntriesExtracted / TotalEntries, 0d, 1d) : 0d;
}

/// <summary>
/// The outcome of an extraction.
/// </summary>
/// <param name="DestinationDirectory">Folder the archive was written into.</param>
/// <param name="EntriesExtracted">How many entries were written.</param>
/// <param name="EntriesRejected">
/// How many entries were refused because their path escaped the destination.
/// </param>
/// <param name="TotalBytes">Total bytes written.</param>
public sealed record ExtractionResult(
    string DestinationDirectory,
    int EntriesExtracted,
    int EntriesRejected,
    long TotalBytes);

/// <summary>
/// Extracts downloaded archives.
/// </summary>
public interface IArchiveExtractionService
{
    /// <summary>
    /// Determines whether a file looks like an archive this service can open.
    /// </summary>
    /// <param name="path">Path to the candidate file.</param>
    /// <returns><see langword="true"/> when the extension is a supported archive format.</returns>
    bool IsSupportedArchive(string path);

    /// <summary>
    /// Extracts an archive into a folder.
    /// </summary>
    /// <param name="archivePath">Archive to read.</param>
    /// <param name="destinationDirectory">Folder to write into. Created if missing.</param>
    /// <param name="progress">Optional receiver for progress updates.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>Details of what was written.</returns>
    /// <exception cref="ArgumentException">A path argument is null or blank.</exception>
    /// <exception cref="FileNotFoundException">The archive does not exist.</exception>
    /// <exception cref="InvalidOperationException">The archive could not be read.</exception>
    Task<ExtractionResult> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
