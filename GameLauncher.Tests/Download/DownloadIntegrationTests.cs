using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Download;

/// <summary>
/// Exercises the download path end to end against a real HTTP server.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests beside this file cover the pieces in isolation — file name
/// sanitisation, path resolution. These cover the thing those cannot: that bytes
/// actually move over a socket correctly, that a dropped connection leaves
/// something resumable, that a range request is sent and honoured, and that a
/// redirect chain is followed with its headers intact.
/// </para>
/// <para>
/// The download service is resolved from the real container so the transfer runs
/// on the configured client, including its deliberately infinite timeout. A
/// substituted <see cref="HttpMessageHandler"/> would leave both untested.
/// </para>
/// </remarks>
public sealed class DownloadIntegrationTests : IDisposable
{
    /// <summary>Payload size used throughout; large enough to span many chunks.</summary>
    private const int PayloadSize = 512 * 1024;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));

    private readonly byte[] _payload = CreatePayload(PayloadSize);

    public DownloadIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task A_fresh_download_arrives_intact()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var reports = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(reports.Add);

        var result = await service.DownloadAsync(
            new DownloadRequest
            {
                Url = server.FileUrl("game.bin"),
                DestinationDirectory = _root,
                FileName = "game.bin"
            },
            progress);

        Assert.Equal(PayloadSize, result.TotalBytes);
        Assert.Equal(PayloadSize, result.BytesTransferred);
        Assert.False(result.WasResumed);
        Assert.False(result.ChecksumVerified);

        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));

        // The temporary file is renamed, never left beside the finished one.
        Assert.False(File.Exists(result.FilePath + ".part"));

        // Progress is reported and ends at the full size. Awaiting a Progress<T>
        // callback is not synchronous, so the final report may still be in flight;
        // what matters is that the size was known and reports were made.
        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.Equal(PayloadSize, report.TotalBytes));
    }

    [Fact]
    public async Task A_dropped_connection_leaves_a_partial_file_to_resume_from()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);
        server.DropConnectionAfterBytes = 64 * 1024;

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var failure = await Record.ExceptionAsync(() => service.DownloadAsync(new DownloadRequest
        {
            Url = server.UnstableUrl("game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin"
        }));

        // A reset mid-body surfaces as an IO failure, not a clean short read.
        Assert.NotNull(failure);
        Assert.True(
            failure is IOException or HttpRequestException,
            $"Expected a transfer failure but got {failure!.GetType().Name}: {failure.Message}");

        var partPath = Path.Combine(_root, "game.bin.part");

        // What arrived is kept: the point of the .part file is that the next
        // attempt continues rather than starting over.
        Assert.True(File.Exists(partPath), "The interrupted transfer left nothing to resume from.");

        var partial = new FileInfo(partPath).Length;
        Assert.InRange(partial, 1, PayloadSize - 1);

        // The finished path stays empty, so nothing can mistake a truncated file
        // for a complete one.
        Assert.False(File.Exists(Path.Combine(_root, "game.bin")));
    }

    [Fact]
    public async Task An_existing_partial_is_continued_with_a_range_request()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        const int Already = 100 * 1024;
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin.part"), _payload[..Already]);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var result = await service.DownloadAsync(new DownloadRequest
        {
            Url = server.FileUrl("game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin"
        });

        Assert.True(result.WasResumed);

        // Only the remainder crossed the network — the whole point of resuming.
        Assert.Equal(PayloadSize - Already, result.BytesTransferred);
        Assert.Equal(PayloadSize, result.TotalBytes);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));

        var ranged = server.Requests.Single(request => request.Range is not null);
        Assert.Equal($"bytes={Already}-", ranged.Range);
    }

    [Fact]
    public async Task An_interrupted_download_completes_when_it_is_retried()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);
        server.DropConnectionAfterBytes = 96 * 1024;

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var request = new DownloadRequest
        {
            Url = server.UnstableUrl("game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin"
        };

        Assert.NotNull(await Record.ExceptionAsync(() => service.DownloadAsync(request)));

        var recovered = new FileInfo(Path.Combine(_root, "game.bin.part")).Length;
        Assert.InRange(recovered, 1, PayloadSize - 1);

        // The server stops dropping; the retry picks up where the first left off.
        server.DropConnectionAfterBytes = null;

        var result = await service.DownloadAsync(request);

        Assert.True(result.WasResumed);
        Assert.Equal(PayloadSize, result.TotalBytes);

        // Byte-for-byte identical to the source, which is what proves the two
        // halves were joined at the right offset rather than merely adding up.
        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task A_server_that_ignores_ranges_restarts_instead_of_appending()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin.part"), _payload[..(80 * 1024)]);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var result = await service.DownloadAsync(new DownloadRequest
        {
            Url = server.NoRangeUrl("game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin"
        });

        // The route advertises Accept-Ranges and then ignores the header, so
        // trusting the advertisement would append a whole second copy to the
        // 80 KB already there. The 200 is the only reliable signal.
        Assert.False(result.WasResumed);
        Assert.Equal(PayloadSize, result.TotalBytes);
        Assert.Equal(PayloadSize, result.BytesTransferred);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task A_redirect_chain_is_followed_to_the_file()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var result = await service.DownloadAsync(new DownloadRequest
        {
            Url = server.RedirectUrl(3, "game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin"
        });

        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));

        // Every hop was actually walked, ending at the real file.
        Assert.Equal(3, server.Requests.Count(request => request.Path.StartsWith("/redirect/", StringComparison.Ordinal)));
        Assert.Contains(server.Requests, request => request.Path == "/files/game.bin");
    }

    [Fact]
    public async Task A_resume_survives_a_redirect()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        const int Already = 120 * 1024;
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin.part"), _payload[..Already]);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var result = await service.DownloadAsync(new DownloadRequest
        {
            Url = server.RedirectUrl(2, "game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin"
        });

        Assert.True(result.WasResumed);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));

        // The Range header has to survive being replayed against the redirect
        // target, or a resumed download quietly turns into a restarted one.
        var final = server.Requests.Single(request => request.Path == "/files/game.bin");
        Assert.Equal($"bytes={Already}-", final.Range);
    }

    [Fact]
    public async Task A_matching_checksum_is_reported_as_verified()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var result = await service.DownloadAsync(new DownloadRequest
        {
            Url = server.FileUrl("game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin",
            ExpectedChecksum = Sha256(_payload)
        });

        Assert.True(result.ChecksumVerified);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task A_checksum_mismatch_deletes_the_download_rather_than_keeping_it()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAsync(new DownloadRequest
            {
                Url = server.FileUrl("game.bin"),
                DestinationDirectory = _root,
                FileName = "game.bin",
                ExpectedChecksum = Sha256("something else"u8.ToArray())
            }));

        Assert.Contains("checksum", failure.Message, StringComparison.OrdinalIgnoreCase);

        // Neither file survives. Keeping the partial would mean the next attempt
        // resumed from bytes already known to be wrong, and resuming corruption
        // never converges.
        Assert.False(File.Exists(Path.Combine(_root, "game.bin.part")));
        Assert.False(File.Exists(Path.Combine(_root, "game.bin")));
    }

    [Fact]
    public async Task A_corrupt_partial_is_discarded_and_the_next_attempt_succeeds()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        // A partial of the right length but the wrong bytes: the shape a
        // half-finished download from a different build, or a damaged disk, leaves.
        var corrupt = new byte[64 * 1024];
        Array.Fill(corrupt, (byte)0xEE);
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin.part"), corrupt);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var request = new DownloadRequest
        {
            Url = server.FileUrl("game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin",
            ExpectedChecksum = Sha256(_payload)
        };

        // Resuming on top of it produces a file that fails verification, which is
        // detected rather than shipped.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(request));
        Assert.False(File.Exists(Path.Combine(_root, "game.bin.part")));

        // Because the bad partial was deleted, the retry starts clean and works.
        var result = await service.DownloadAsync(request);

        Assert.True(result.ChecksumVerified);
        Assert.False(result.WasResumed);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task Cancelling_leaves_a_partial_file_and_a_restart_finishes_it()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        // Slowed so the transfer is reliably still running when it is cancelled.
        server.ChunkSize = 16 * 1024;
        server.ChunkDelay = TimeSpan.FromMilliseconds(20);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var request = new DownloadRequest
        {
            Url = server.FileUrl("game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin"
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAsync(request, progress: null, cancellation.Token));

        var partPath = Path.Combine(_root, "game.bin.part");
        Assert.True(File.Exists(partPath), "Cancelling discarded the bytes already transferred.");
        Assert.InRange(new FileInfo(partPath).Length, 1, PayloadSize - 1);

        server.ChunkDelay = TimeSpan.Zero;

        var result = await service.DownloadAsync(request);

        Assert.True(result.WasResumed);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task A_hostile_content_disposition_name_cannot_escape_the_download_folder()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.bin", _payload);

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        var destination = Path.Combine(_root, "downloads");

        // The header is chosen by whoever runs the server, so this is the real
        // shape of the attack rather than a hypothetical one.
        var result = await service.DownloadAsync(new DownloadRequest
        {
            Url = server.NamedUrl("game.bin", @"..\..\Startup\evil.exe"),
            DestinationDirectory = destination
        });

        Assert.Equal(destination, Path.GetDirectoryName(result.FilePath));
        Assert.Equal("evil.exe", Path.GetFileName(result.FilePath));
        Assert.True(File.Exists(Path.Combine(destination, "evil.exe")));
    }

    [Fact]
    public async Task A_missing_file_fails_without_leaving_anything_behind()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        using var host = new TestAppHost();
        var service = host.Resolve<IDownloadService>();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(new DownloadRequest
        {
            Url = new Uri(server.BaseAddress, "/missing/game.bin"),
            DestinationDirectory = _root,
            FileName = "game.bin"
        }));

        Assert.False(File.Exists(Path.Combine(_root, "game.bin")));
        Assert.False(File.Exists(Path.Combine(_root, "game.bin.part")));
    }

    [Fact]
    public async Task A_downloaded_archive_extracts_to_the_files_it_contains()
    {
        var archive = CreateZip(new Dictionary<string, string>
        {
            ["game/game.exe"] = "executable bytes",
            ["game/data/assets.pak"] = "asset bytes",
            ["readme.txt"] = "read me"
        });

        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.zip", archive);

        using var host = new TestAppHost();
        var downloads = host.Resolve<IDownloadService>();
        var extraction = host.Resolve<IArchiveExtractionService>();

        var download = await downloads.DownloadAsync(new DownloadRequest
        {
            Url = server.FileUrl("game.zip"),
            DestinationDirectory = _root,
            FileName = "game.zip",
            ExpectedChecksum = Sha256(archive)
        });

        Assert.True(download.ChecksumVerified);
        Assert.True(extraction.IsSupportedArchive(download.FilePath));

        var destination = Path.Combine(_root, "install");
        var result = await extraction.ExtractAsync(download.FilePath, destination);

        Assert.Equal(3, result.EntriesExtracted);
        Assert.Equal(0, result.EntriesRejected);

        Assert.Equal("executable bytes", await File.ReadAllTextAsync(Path.Combine(destination, "game", "game.exe")));
        Assert.Equal("asset bytes", await File.ReadAllTextAsync(Path.Combine(destination, "game", "data", "assets.pak")));
        Assert.Equal("read me", await File.ReadAllTextAsync(Path.Combine(destination, "readme.txt")));
    }

    [Fact]
    public async Task A_downloaded_archive_cannot_write_outside_its_destination()
    {
        var archive = CreateZip(new Dictionary<string, string>
        {
            ["game/game.exe"] = "executable bytes",
            ["../escaped.txt"] = "should never be written",
            ["../../Startup/evil.exe"] = "should never be written"
        });

        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("hostile.zip", archive);

        using var host = new TestAppHost();

        var download = await host.Resolve<IDownloadService>().DownloadAsync(new DownloadRequest
        {
            Url = server.FileUrl("hostile.zip"),
            DestinationDirectory = _root,
            FileName = "hostile.zip"
        });

        var destination = Path.Combine(_root, "install");
        var result = await host.Resolve<IArchiveExtractionService>().ExtractAsync(download.FilePath, destination);

        // The safe entry lands; both escaping entries are refused rather than the
        // whole archive being rejected, so a hostile entry cannot deny the user a
        // legitimate download.
        Assert.Equal(1, result.EntriesExtracted);
        Assert.Equal(2, result.EntriesRejected);

        Assert.True(File.Exists(Path.Combine(destination, "game", "game.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "Startup", "evil.exe")));

        // Nothing at all was written above the destination.
        var strays = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(destination, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Equal(["hostile.zip"], strays);
    }

    /// <summary>Builds deterministic pseudo-random content.</summary>
    /// <param name="size">How many bytes to produce.</param>
    /// <returns>The payload.</returns>
    /// <remarks>
    /// Seeded rather than random so a failure can be reproduced, and varied rather
    /// than uniform so a transfer that joins two halves at the wrong offset
    /// produces a different file rather than an identical one.
    /// </remarks>
    private static byte[] CreatePayload(int size)
    {
        var payload = new byte[size];
        new Random(20260804).NextBytes(payload);
        return payload;
    }

    /// <summary>Computes a lowercase hex SHA-256 digest.</summary>
    /// <param name="content">Bytes to hash.</param>
    /// <returns>The digest.</returns>
    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>Builds a zip archive in memory.</summary>
    /// <param name="entries">Entry names mapped to their text content.</param>
    /// <returns>The archive bytes.</returns>
    private static byte[] CreateZip(IDictionary<string, string> entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                // CreateEntry does not validate the name, which is what lets a
                // traversal entry be written here exactly as a hostile archive
                // would carry it.
                var entry = archive.CreateEntry(name);

                using var stream = entry.Open();
                using var writer = new StreamWriter(stream);
                writer.Write(content);
            }
        }

        return buffer.ToArray();
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
