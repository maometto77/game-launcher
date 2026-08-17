using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Download;

/// <summary>
/// Covers the transport seam: how a payload is classified, which engine is
/// chosen for it, and what happens when the preferred one is not installed.
/// </summary>
public sealed class DownloadTransportTests
{
    [Theory]
    [InlineData("https://example.test/game.zip", DownloadPayload.Http)]
    [InlineData("http://example.test/game.7z", DownloadPayload.Http)]
    [InlineData("https://archive.org/download/x/x_archive.torrent", DownloadPayload.Torrent)]
    [InlineData("https://example.test/Path.TORRENT", DownloadPayload.Torrent)]
    [InlineData("magnet:?xt=urn:btih:abcdef", DownloadPayload.Torrent)]
    public void A_payload_is_classified_from_its_address(string url, DownloadPayload expected) =>
        Assert.Equal(expected, DownloadService.ClassifyPayload(new Uri(url)));

    [Fact]
    public async Task An_http_download_uses_the_only_available_transport()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.zip", new byte[2048]);

        using var temp = new TempDirectory();

        var http = new RecordingTransport(TransportCapabilities.Http, available: true, priority: 100);
        var service = Service(http);

        var result = await service.DownloadAsync(new DownloadRequest
        {
            Url = server.FileUrl("game.zip"),
            DestinationDirectory = temp.Path
        });

        Assert.Equal(1, http.Transfers);
        Assert.True(File.Exists(result.FilePath));
    }

    [Fact]
    public async Task The_better_transport_wins_when_it_is_available()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.zip", new byte[1024]);

        using var temp = new TempDirectory();

        var preferred = new RecordingTransport(TransportCapabilities.Http, available: true, priority: 0);
        var fallback = new RecordingTransport(TransportCapabilities.Http, available: true, priority: 100);

        await Service(preferred, fallback).DownloadAsync(new DownloadRequest
        {
            Url = server.FileUrl("game.zip"),
            DestinationDirectory = temp.Path
        });

        Assert.Equal(1, preferred.Transfers);
        Assert.Equal(0, fallback.Transfers);
    }

    [Fact]
    public async Task An_unavailable_transport_falls_through_to_the_next()
    {
        // The whole point of the availability check: aria2c not being installed
        // must cost the user nothing at all.
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.zip", new byte[1024]);

        using var temp = new TempDirectory();

        var missing = new RecordingTransport(TransportCapabilities.Http, available: false, priority: 0);
        var fallback = new RecordingTransport(TransportCapabilities.Http, available: true, priority: 100);

        await Service(missing, fallback).DownloadAsync(new DownloadRequest
        {
            Url = server.FileUrl("game.zip"),
            DestinationDirectory = temp.Path
        });

        Assert.Equal(0, missing.Transfers);
        Assert.Equal(1, fallback.Transfers);
    }

    [Fact]
    public async Task A_torrent_without_a_capable_transport_explains_itself()
    {
        using var temp = new TempDirectory();

        var service = Service(new RecordingTransport(TransportCapabilities.Http, true, 100));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAsync(new DownloadRequest
            {
                Url = new Uri("magnet:?xt=urn:btih:abcdef"),
                DestinationDirectory = temp.Path
            }));

        // Names what is missing and what to do instead, rather than failing with
        // a scheme error the user cannot act on.
        Assert.Contains("aria2c", exception.Message);
        Assert.Contains("HTTP mirror", exception.Message);
    }

    [Fact]
    public async Task A_magnet_address_is_accepted_once_something_can_move_it()
    {
        using var temp = new TempDirectory();

        var torrent = new RecordingTransport(TransportCapabilities.Torrent, available: true, priority: 0)
        {
            ProducesPath = Path.Combine(temp.Path, "payload.bin")
        };

        File.WriteAllBytes(torrent.ProducesPath, new byte[64]);

        var result = await Service(torrent).DownloadAsync(new DownloadRequest
        {
            Url = new Uri("magnet:?xt=urn:btih:abcdef"),
            DestinationDirectory = temp.Path
        });

        Assert.Equal(1, torrent.Transfers);
        Assert.Equal(torrent.ProducesPath, result.FilePath);
        Assert.Equal(64, result.TotalBytes);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/notepad.exe")]
    [InlineData("ftp://example.test/game.zip")]
    public async Task Other_schemes_are_still_refused(string url)
    {
        using var temp = new TempDirectory();

        var service = Service(new RecordingTransport(TransportCapabilities.Http, true, 100));

        // A downloader that accepts file:// turns a pasted string into an
        // arbitrary local file copy.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DownloadAsync(new DownloadRequest
            {
                Url = new Uri(url),
                DestinationDirectory = temp.Path
            }));
    }

    [Fact]
    public void Registering_no_transport_at_all_fails_loudly()
    {
        using var host = new TestAppHost();

        Assert.Throws<InvalidOperationException>(() => new DownloadService(
            [],
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<DownloadService>.Instance));
    }

    [Fact]
    public async Task Aria2_reports_itself_unavailable_until_it_is_switched_on()
    {
        var settings = new StubSettings();

        using var host = new TestAppHost();

        var transport = new Aria2DownloadTransport(
            settings,
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<Aria2DownloadTransport>.Instance);

        // Off by default: starting an external process is a decision worth
        // making explicitly, not one inherited from a binary being on the path.
        Assert.False(await transport.IsAvailableAsync());

        // Enabled but pointed at something that does not exist still reports
        // unavailable rather than throwing when a download is attempted.
        settings.Set(settings.Current with
        {
            Aria2Enabled = true,
            Aria2ExecutablePath = Path.Combine(Path.GetTempPath(), "definitely-not-aria2c.exe")
        });

        Assert.False(await transport.IsAvailableAsync());
    }

    [Fact]
    public void Aria2_declares_both_capabilities()
    {
        using var host = new TestAppHost();

        var transport = new Aria2DownloadTransport(
            new StubSettings(),
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<Aria2DownloadTransport>.Instance);

        Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.Http));
        Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.Torrent));

        // Preferred over the built-in engine, which is the floor.
        Assert.True(transport.Priority < 100);
    }

    [Fact]
    public async Task A_transport_that_cannot_start_hands_the_work_to_the_next_one()
    {
        // Availability is answered before the work begins and can be wrong by the
        // time it starts — an executable that answered --version a moment ago may
        // have been moved since. Nothing was transferred, so the next engine
        // cannot fetch the file twice.
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.zip", new byte[1024]);

        using var temp = new TempDirectory();

        var broken = new UnstartableTransport(priority: 0);
        var working = new RecordingTransport(TransportCapabilities.Http, available: true, priority: 100);

        var result = await Service(broken, working).DownloadAsync(new DownloadRequest
        {
            Url = server.FileUrl("game.zip"),
            DestinationDirectory = temp.Path
        });

        Assert.Equal(1, broken.Attempts);
        Assert.Equal(1, working.Transfers);
        Assert.True(File.Exists(result.FilePath));
    }

    [Fact]
    public async Task The_last_transport_failing_to_start_is_reported_rather_than_swallowed()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("game.zip", new byte[1024]);

        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<TransportUnavailableException>(() =>
            Service(new UnstartableTransport(priority: 0)).DownloadAsync(new DownloadRequest
            {
                Url = server.FileUrl("game.zip"),
                DestinationDirectory = temp.Path
            }));
    }

    [Fact]
    public async Task Aria2_is_asked_to_open_its_rpc_interface_on_loopback()
    {
        // Stands in for aria2c: records the command line it was given and exits.
        // What matters is that RPC is switched on, bound to loopback, and given a
        // secret worth having.
        using var temp = new TempDirectory();

        var log = Path.Combine(temp.Path, "argv.txt");
        var stub = Path.Combine(temp.Path, "aria2c-stub.cmd");

        await File.WriteAllTextAsync(
            stub,
            "@echo off\r\n" +
            $"echo %* > \"{log}\"\r\n" +
            "exit /b 0\r\n");

        var settings = new StubSettings();

        settings.Set(settings.Current with { Aria2Enabled = true, Aria2ExecutablePath = stub });

        using var host = new TestAppHost();

        var transport = new Aria2DownloadTransport(
            settings,
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<Aria2DownloadTransport>.Instance);

        var part = Path.Combine(temp.Path, "game.zip.part");

        await File.WriteAllBytesAsync(part, new byte[64]);

        await transport.TransferAsync(new TransportRequest
        {
            Url = new Uri("https://example.test/game.zip"),
            Payload = DownloadPayload.Http,
            PartPath = part,
            DestinationDirectory = temp.Path
        });

        var argv = await File.ReadAllTextAsync(log);

        Assert.Contains("--enable-rpc=true", argv, StringComparison.Ordinal);
        Assert.Contains("--rpc-listen-all=false", argv, StringComparison.Ordinal);

        // 256 bits of hex, fresh for this transfer.
        var secret = System.Text.RegularExpressions.Regex.Match(argv, "--rpc-secret=([0-9a-f]+)");

        Assert.True(secret.Success);
        Assert.Equal(64, secret.Groups[1].Value.Length);

        // A port was reserved and passed, rather than a fixed one that a second
        // launcher would collide with.
        Assert.Matches("--rpc-listen-port=[0-9]+", argv);
    }

    [Fact]
    public async Task A_transfer_still_reports_progress_when_the_rpc_interface_never_answers()
    {
        // The statistics are a poll on the side. Nothing listens on the port this
        // stub was handed, and the download must proceed regardless — falling
        // back to measuring the file, which is what it did before RPC existed.
        using var temp = new TempDirectory();

        var part = Path.Combine(temp.Path, "game.zip.part");
        var stub = Path.Combine(temp.Path, "aria2c-quiet.cmd");

        // Answers the availability probe at once, then lingers so the reporter
        // gets several turns with nothing listening on the RPC port.
        await File.WriteAllTextAsync(
            stub,
            "@echo off\r\n" +
            "if \"%1\"==\"--version\" exit /b 0\r\n" +
            "ping -n 5 127.0.0.1 >nul\r\n" +
            "exit /b 0\r\n");

        // A part file with its control file beside it: what an interrupted aria2
        // transfer leaves behind, and the only shape the transport treats as
        // resumable rather than deleting and starting again.
        await File.WriteAllBytesAsync(part, new byte[4096]);
        await File.WriteAllBytesAsync(part + ".aria2", new byte[16]);

        var settings = new StubSettings();

        settings.Set(settings.Current with { Aria2Enabled = true, Aria2ExecutablePath = stub });

        using var host = new TestAppHost();

        var transport = new Aria2DownloadTransport(
            settings,
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<Aria2DownloadTransport>.Instance);

        // Collected synchronously rather than through Progress<T>, whose callback
        // is posted to whatever context happens to be current — a timing
        // dependence this test has no reason to take on.
        var reports = new CollectingProgress();

        await transport.TransferAsync(
            new TransportRequest
            {
                Url = new Uri("https://example.test/game.zip"),
                Payload = DownloadPayload.Http,
                PartPath = part,
                DestinationDirectory = temp.Path
            },
            reports);

        var collected = reports.Reports;

        Assert.NotEmpty(collected);
        Assert.Contains(collected, report => report.BytesReceived > 0);

        // Nothing answered, so there is nothing honest to say about peers.
        Assert.All(collected, report => Assert.Null(report.Peers));
    }

    [Fact]
    public async Task Peer_and_seed_counts_from_the_rpc_interface_reach_the_progress_stream()
    {
        // The whole point of the change, end to end through the real transport:
        // it launches a process, tells it which port to serve RPC on, polls that
        // port, and turns what comes back into progress the Downloads table can
        // show. The stub stands in for aria2c and this test stands in for its RPC
        // interface, on the very port the transport chose.
        using var temp = new TempDirectory();

        var argv = Path.Combine(temp.Path, "argv.txt");
        var stub = Path.Combine(temp.Path, "aria2c-rpc.cmd");

        await File.WriteAllTextAsync(
            stub,
            "@echo off\r\n" +
            "if \"%1\"==\"--version\" exit /b 0\r\n" +
            $"echo %* > \"{argv}\"\r\n" +
            "ping -n 8 127.0.0.1 >nul\r\n" +
            "exit /b 0\r\n");

        var settings = new StubSettings();

        settings.Set(settings.Current with { Aria2Enabled = true, Aria2ExecutablePath = stub });

        using var host = new TestAppHost();

        var transport = new Aria2DownloadTransport(
            settings,
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<Aria2DownloadTransport>.Instance);

        var part = Path.Combine(temp.Path, "game.zip.part");
        var reports = new CollectingProgress();

        var transfer = transport.TransferAsync(
            new TransportRequest
            {
                Url = new Uri("https://example.test/game.zip"),
                Payload = DownloadPayload.Http,
                PartPath = part,
                DestinationDirectory = temp.Path
            },
            reports);

        // The transport released the port before handing it over, so it is free
        // to bind here — which is exactly how the real aria2c gets it too.
        await using var rpc = await LoopbackRpcServer.StartAsync(await ReadPortAsync(argv));

        rpc.ResponseBody =
            """
            {"id":"gl","jsonrpc":"2.0","result":[{
              "gid":"a1","completedLength":"34359738","totalLength":"343597383",
              "downloadSpeed":"1048576","connections":"27","numSeeders":"4"
            }]}
            """;

        await transfer;

        var collected = reports.Reports;

        Assert.Contains(collected, report => report.Peers == 27 && report.Seeders == 4);

        // The size aria2 reports is something no amount of watching a file could
        // produce, and it is what turns an indeterminate bar into a real one.
        Assert.Contains(collected, report => report.TotalBytes == 343597383);
        Assert.Contains(collected, report => report.BytesPerSecond == 1048576);
    }

    [Fact]
    public async Task Closing_the_launcher_does_not_leave_aria2c_running()
    {
        // Cancelling a transfer asks its process to stop, but the cancellation
        // and the process's death are separate events, and an application that
        // has already exited is not around to see the second one. A child process
        // on Windows outlives its parent unless something ends it.
        using var temp = new TempDirectory();

        var argv = Path.Combine(temp.Path, "argv.txt");
        var stub = Path.Combine(temp.Path, "aria2c-forever.cmd");

        // Would run for half a minute if nothing stopped it.
        await File.WriteAllTextAsync(
            stub,
            "@echo off\r\n" +
            "if \"%1\"==\"--version\" exit /b 0\r\n" +
            $"echo %* > \"{argv}\"\r\n" +
            "ping -n 30 127.0.0.1 >nul\r\n" +
            "exit /b 0\r\n");

        var settings = new StubSettings();

        settings.Set(settings.Current with { Aria2Enabled = true, Aria2ExecutablePath = stub });

        using var host = new TestAppHost();

        var transport = new Aria2DownloadTransport(
            settings,
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<Aria2DownloadTransport>.Instance);

        var transfer = transport.TransferAsync(new TransportRequest
        {
            Url = new Uri("https://example.test/game.zip"),
            Payload = DownloadPayload.Http,
            PartPath = Path.Combine(temp.Path, "game.zip.part"),
            DestinationDirectory = temp.Path
        });

        // Started, and now running.
        await ReadPortAsync(argv);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        transport.Dispose();

        // It ended because it was ended, not because it finished: the stub had
        // most of half a minute left to run.
        await Assert.ThrowsAnyAsync<Exception>(() => transfer);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"the process outlived disposal by {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Waits for the stub to record its command line, and reads the RPC port out
    /// of it.
    /// </summary>
    /// <param name="argvPath">File the stub writes its arguments to.</param>
    /// <returns>The port the transport chose.</returns>
    private static async Task<int> ReadPortAsync(string argvPath)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(argvPath))
            {
                string text;

                try
                {
                    text = await File.ReadAllTextAsync(argvPath);
                }
                catch (IOException)
                {
                    // Still being written by the stub.
                    await Task.Delay(50);
                    continue;
                }

                var match = System.Text.RegularExpressions.Regex.Match(text, "--rpc-listen-port=([0-9]+)");

                if (match.Success)
                {
                    return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("The stub never recorded an RPC port.");
    }

    private static DownloadService Service(params IDownloadTransport[] transports)
    {
        var host = new TestAppHost();

        return new DownloadService(
            transports,
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<DownloadService>.Instance);
    }

    /// <summary>Collects progress reports on the thread that raises them.</summary>
    private sealed class CollectingProgress : IProgress<DownloadProgress>
    {
        private readonly List<DownloadProgress> _reports = [];

        public IReadOnlyList<DownloadProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return _reports.ToArray();
                }
            }
        }

        public void Report(DownloadProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }

    /// <summary>A transport that says it is available and then will not start.</summary>
    private sealed class UnstartableTransport(int priority) : IDownloadTransport
    {
        public string Name => "unstartable";

        public TransportCapabilities Capabilities => TransportCapabilities.Http | TransportCapabilities.Torrent;

        public int Priority { get; } = priority;

        public int Attempts { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<TransportOutcome> TransferAsync(
            TransportRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new TransportUnavailableException("the engine could not be started.");
        }
    }

    /// <summary>A transport that records what it was asked to do.</summary>
    private sealed class RecordingTransport(
        TransportCapabilities capabilities,
        bool available,
        int priority) : IDownloadTransport
    {
        public string Name => $"recording({Capabilities})";

        public TransportCapabilities Capabilities { get; } = capabilities;

        public int Priority { get; } = priority;

        public int Transfers { get; private set; }

        /// <summary>Path a torrent transfer claims to have produced.</summary>
        public string? ProducesPath { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(available);

        public Task<TransportOutcome> TransferAsync(
            TransportRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Transfers++;

            if (request.Payload == DownloadPayload.Torrent)
            {
                return Task.FromResult(new TransportOutcome(ProducesPath!, 64, false));
            }

            // Stands in for the real transfer so the service's own steps —
            // checksum, rename, result — still run against a real file.
            File.WriteAllBytes(request.PartPath, new byte[128]);

            return Task.FromResult(new TransportOutcome(request.PartPath, 128, false));
        }
    }

    /// <summary>Settings held in memory, with no file behind them.</summary>
    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();

        public event EventHandler<AppSettings>? SettingsChanged;

        public void Set(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
        }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Set(settings);
            return Task.CompletedTask;
        }
    }

    /// <summary>A temporary directory removed when the test finishes.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
}
