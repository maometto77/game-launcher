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

        var transport = new Aria2DownloadTransport(
            settings, NullLogger<Aria2DownloadTransport>.Instance);

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
        var transport = new Aria2DownloadTransport(
            new StubSettings(), NullLogger<Aria2DownloadTransport>.Instance);

        Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.Http));
        Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.Torrent));

        // Preferred over the built-in engine, which is the floor.
        Assert.True(transport.Priority < 100);
    }

    private static DownloadService Service(params IDownloadTransport[] transports)
    {
        var host = new TestAppHost();

        return new DownloadService(
            transports,
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            NullLogger<DownloadService>.Instance);
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
