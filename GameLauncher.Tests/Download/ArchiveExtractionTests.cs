using System.IO.Compression;
using GameLauncher.Desktop.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Download;

/// <summary>
/// Covers archive extraction, with particular attention to path traversal.
/// </summary>
public sealed class ArchiveExtractionTests : IDisposable
{
    private readonly string _root;
    private readonly ArchiveExtractionService _service;

    public ArchiveExtractionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new ArchiveExtractionService(NullLogger<ArchiveExtractionService>.Instance);
    }

    [Theory]
    [InlineData("game/data.bin")]
    [InlineData("game\\nested\\deep\\asset.pak")]
    [InlineData("readme.txt")]
    // A leading separator is stripped and the entry treated as relative. Archives
    // written by some tools store paths this way, and the result still lands
    // inside the destination, so refusing them would break valid archives for no
    // security gain.
    [InlineData("/absolute/path.txt")]
    public void Ordinary_entries_resolve_inside_the_destination(string entryKey)
    {
        var destination = Path.Combine(_root, "install");

        var resolved = ArchiveExtractionService.TryResolveEntryPath(destination, entryKey, out var target);

        Assert.True(resolved);
        Assert.StartsWith(destination, target, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("..\\..\\Startup\\evil.exe")]
    [InlineData("game/../../../outside.dll")]
    // A drive-qualified path has no leading separator to strip, so it stays
    // rooted and is rejected outright.
    [InlineData("C:\\Windows\\System32\\evil.dll")]
    [InlineData("")]
    public void Traversal_entries_are_refused(string entryKey)
    {
        var destination = Path.Combine(_root, "install");

        var resolved = ArchiveExtractionService.TryResolveEntryPath(destination, entryKey, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void Sibling_directory_with_a_shared_prefix_is_refused()
    {
        // "install-evil" starts with "install", so a naive StartsWith check
        // without a separator would wrongly accept this.
        var destination = Path.Combine(_root, "install");

        var resolved = ArchiveExtractionService.TryResolveEntryPath(destination, "../install-evil/x.txt", out _);

        Assert.False(resolved);
    }

    [Fact]
    public async Task Zip_archive_is_extracted()
    {
        var archivePath = Path.Combine(_root, "sample.zip");
        var destination = Path.Combine(_root, "out");

        CreateZip(archivePath, new Dictionary<string, string>
        {
            ["game/game.exe"] = "binary",
            ["game/data/assets.pak"] = "assets",
            ["readme.txt"] = "hello"
        });

        var result = await _service.ExtractAsync(archivePath, destination);

        Assert.Equal(3, result.EntriesExtracted);
        Assert.Equal(0, result.EntriesRejected);
        Assert.True(File.Exists(Path.Combine(destination, "game", "game.exe")));
        Assert.True(File.Exists(Path.Combine(destination, "game", "data", "assets.pak")));
        Assert.True(File.Exists(Path.Combine(destination, "readme.txt")));
    }

    [Fact]
    public async Task Traversal_entry_in_a_real_archive_is_skipped_without_writing_outside()
    {
        var archivePath = Path.Combine(_root, "evil.zip");
        var destination = Path.Combine(_root, "out");
        var escapeTarget = Path.Combine(_root, "escaped.txt");

        CreateZip(archivePath, new Dictionary<string, string>
        {
            ["good.txt"] = "fine",
            ["../escaped.txt"] = "should never be written"
        });

        var result = await _service.ExtractAsync(archivePath, destination);

        Assert.Equal(1, result.EntriesExtracted);
        Assert.Equal(1, result.EntriesRejected);
        Assert.True(File.Exists(Path.Combine(destination, "good.txt")));

        // The whole point: nothing landed outside the destination folder.
        Assert.False(File.Exists(escapeTarget));
    }

    [Fact]
    public async Task Every_entry_of_a_many_entry_archive_is_written_exactly_once()
    {
        var archivePath = Path.Combine(_root, "many.zip");
        var destination = Path.Combine(_root, "out");

        // Enough entries that a per-entry decode would be obvious, and enough to
        // exercise the progress throttle.
        var entries = Enumerable.Range(0, 400)
            .ToDictionary(index => $"data/file{index:D4}.bin", index => $"contents of entry {index}");

        CreateZip(archivePath, entries);

        var reports = new List<ExtractionProgress>();
        var progress = new Progress<ExtractionProgress>(reports.Add);

        var result = await _service.ExtractAsync(archivePath, destination, progress);

        Assert.Equal(400, result.EntriesExtracted);
        Assert.Equal(0, result.EntriesRejected);
        Assert.Equal(400, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);

        // Content is checked, not just the count: a reader that mismatched entries
        // to names would produce the right number of files with the wrong bodies.
        Assert.Equal("contents of entry 0", await File.ReadAllTextAsync(Path.Combine(destination, "data", "file0000.bin")));
        Assert.Equal("contents of entry 399", await File.ReadAllTextAsync(Path.Combine(destination, "data", "file0399.bin")));
    }

    [Fact]
    public async Task Progress_is_throttled_rather_than_reported_per_entry()
    {
        var archivePath = Path.Combine(_root, "many.zip");
        var destination = Path.Combine(_root, "out");

        CreateZip(
            archivePath,
            Enumerable.Range(0, 400).ToDictionary(index => $"file{index:D4}.bin", index => index.ToString()));

        var reports = new List<ExtractionProgress>();

        await _service.ExtractAsync(archivePath, destination, new Progress<ExtractionProgress>(reports.Add));

        // Reports are spaced 200 ms apart, so 400 tiny entries cannot produce
        // anything close to 400 of them. Unthrottled, this list would hold one
        // per entry and every one would be a dispatcher post.
        Assert.InRange(reports.Count, 1, 20);

        // However the throttle fell, the last report states the finished total.
        Assert.Equal(400, reports[^1].EntriesExtracted);
        Assert.Equal(400, reports[^1].TotalEntries);
        Assert.Equal(1d, reports[^1].Fraction, precision: 6);
    }

    [Fact]
    public async Task Unreadable_archive_reports_a_usable_error()
    {
        var archivePath = Path.Combine(_root, "broken.zip");
        await File.WriteAllTextAsync(archivePath, "this is definitely not a zip file");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ExtractAsync(archivePath, Path.Combine(_root, "out")));

        Assert.Contains("broken.zip", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("game.zip", true)]
    [InlineData("game.7z", true)]
    [InlineData("game.rar", true)]
    [InlineData("setup.exe", false)]
    [InlineData("notes.txt", false)]
    public void Archive_extensions_are_recognised(string fileName, bool expected)
    {
        Assert.Equal(expected, _service.IsSupportedArchive(fileName));
    }

    /// <summary>
    /// Writes a zip containing the supplied entries.
    /// </summary>
    /// <param name="path">Archive to create.</param>
    /// <param name="entries">Map of entry name to text content.</param>
    /// <remarks>
    /// Built with <see cref="ZipArchive"/> from the framework rather than
    /// SharpCompress, so the extractor under test is never validated against an
    /// archive produced by itself. Entry names are written verbatim, which is
    /// what allows a traversal entry to be constructed at all.
    /// </remarks>
    private static void CreateZip(string path, IReadOnlyDictionary<string, string> entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
