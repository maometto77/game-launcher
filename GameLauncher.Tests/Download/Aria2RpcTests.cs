using GameLauncher.Desktop.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Download;
using GameLauncher.Desktop.Services.Downloads;
using GameLauncher.Desktop.ViewModels;
using GameLauncher.Tests.Infrastructure;

namespace GameLauncher.Tests.Download;

/// <summary>
/// Covers the aria2 JSON-RPC interface: what it reports, how it fails, and how
/// peer counts reach the Downloads table.
/// </summary>
/// <remarks>
/// The RPC half runs against a real socket serving real aria2 response bodies.
/// Everything interesting here is wire format — every number arrives as a string,
/// fields that do not apply are simply absent — and a fake returning objects
/// would test none of it.
/// </remarks>
public sealed class Aria2RpcTests
{
    /// <summary>A torrent in flight, as aria2 actually reports one.</summary>
    private const string TorrentActive =
        """
        {"id":"gl","jsonrpc":"2.0","result":[{
          "gid":"2089b05ecca3d829",
          "completedLength":"34359738",
          "totalLength":"343597383",
          "downloadSpeed":"1048576",
          "connections":"27",
          "numSeeders":"4"
        }]}
        """;

    [Fact]
    public async Task A_torrent_in_flight_reports_its_peers_and_seeds()
    {
        await using var server = await LoopbackRpcServer.StartAsync();
        server.ResponseBody = TorrentActive;

        var status = await Client(server).TellActiveAsync();

        Assert.NotNull(status);
        Assert.Equal(34359738, status.CompletedBytes);
        Assert.Equal(343597383, status.TotalBytes);
        Assert.Equal(1048576, status.BytesPerSecond);

        // The two numbers this whole change exists for.
        Assert.Equal(27, status.Connections);
        Assert.Equal(4, status.Seeders);
    }

    [Fact]
    public async Task The_secret_is_sent_on_every_call()
    {
        await using var server = await LoopbackRpcServer.StartAsync();
        server.ResponseBody = TorrentActive;

        var session = new Aria2RpcSession(server.Port, "abc123");

        await new Aria2RpcClient(new HttpClient(), session).TellActiveAsync();

        var call = Assert.Single(server.Calls);

        Assert.Equal("aria2.tellActive", call.Method);

        // aria2 wants it prefixed; sending the bare secret is rejected.
        Assert.Equal("token:abc123", call.Token);
    }

    [Fact]
    public async Task An_http_transfer_reports_connections_but_no_seeders()
    {
        // numSeeders is absent for anything that is not a torrent, which is
        // exactly the distinction the table wants: no seed count on a plain
        // download rather than a misleading zero.
        await using var server = await LoopbackRpcServer.StartAsync();

        server.ResponseBody =
            """
            {"id":"gl","jsonrpc":"2.0","result":[{
              "gid":"a1","completedLength":"1024","totalLength":"4096",
              "downloadSpeed":"512","connections":"8"
            }]}
            """;

        var status = await Client(server).TellActiveAsync();

        Assert.Equal(8, status!.Connections);
        Assert.Null(status.Seeders);
    }

    [Fact]
    public async Task The_payload_is_reported_rather_than_the_torrent_file_that_named_it()
    {
        // Fetching a .torrent and then fetching what it describes are two active
        // downloads to aria2. The few kilobytes of metadata are not what a person
        // wants to watch.
        await using var server = await LoopbackRpcServer.StartAsync();

        server.ResponseBody =
            """
            {"id":"gl","jsonrpc":"2.0","result":[
              {"gid":"a1","completedLength":"2048","totalLength":"4096","downloadSpeed":"1024","connections":"1"},
              {"gid":"a2","completedLength":"1000","totalLength":"900000000","downloadSpeed":"5000","connections":"30","numSeeders":"6"}
            ]}
            """;

        var status = await Client(server).TellActiveAsync();

        Assert.Equal(900000000, status!.TotalBytes);
        Assert.Equal(30, status.Connections);
    }

    [Fact]
    public async Task A_size_aria2_does_not_know_yet_is_absent_rather_than_zero()
    {
        // aria2 reports "0" before it has the metadata. Passed through as zero it
        // would drive a progress bar that says complete.
        await using var server = await LoopbackRpcServer.StartAsync();

        server.ResponseBody =
            """
            {"id":"gl","jsonrpc":"2.0","result":[{
              "gid":"a1","completedLength":"0","totalLength":"0","downloadSpeed":"0","connections":"0"
            }]}
            """;

        var status = await Client(server).TellActiveAsync();

        Assert.Null(status!.TotalBytes);
        Assert.Equal(0, status.Connections);
    }

    [Fact]
    public async Task Nothing_active_is_not_an_error()
    {
        await using var server = await LoopbackRpcServer.StartAsync();

        // How a finished download, and one that has not started, both look.
        Assert.Null(await Client(server).TellActiveAsync());
    }

    [Fact]
    public async Task A_rejected_secret_is_reported_as_nothing_rather_than_thrown()
    {
        // The transfer is running on the command line and does not depend on this
        // call, so an RPC-level error must not take the download down with it.
        await using var server = await LoopbackRpcServer.StartAsync();

        server.ResponseBody =
            """{"id":"gl","jsonrpc":"2.0","error":{"code":1,"message":"Unauthorized"}}""";

        Assert.Null(await Client(server).TellActiveAsync());
    }

    [Fact]
    public async Task An_unreachable_endpoint_throws_so_the_caller_can_fall_back()
    {
        // Distinct from "nothing active": the transport counts these and reverts
        // to measuring the file, which it must not do just because a download
        // finished.
        var session = new Aria2RpcSession(FreePort(), "secret");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new Aria2RpcClient(client, session).TellActiveAsync());
    }

    [Fact]
    public async Task A_malformed_body_does_not_escape_as_something_unexpected()
    {
        await using var server = await LoopbackRpcServer.StartAsync();
        server.ResponseBody = "not json at all";

        await Assert.ThrowsAsync<JsonException>(() => Client(server).TellActiveAsync());
    }

    [Fact]
    public async Task Shutdown_asks_rather_than_kills()
    {
        await using var server = await LoopbackRpcServer.StartAsync();

        await Client(server).ShutdownAsync();

        // A graceful stop is what leaves aria2's control file intact, and the
        // control file is what makes the next attempt resume rather than restart.
        Assert.Equal("aria2.shutdown", Assert.Single(server.Calls).Method);
    }

    [Fact]
    public void A_session_binds_loopback_only_and_a_fresh_secret_each_time()
    {
        var first = Aria2RpcSession.Create();
        var second = Aria2RpcSession.Create();

        Assert.StartsWith("http://127.0.0.1:", first.Endpoint.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/jsonrpc", first.Endpoint.AbsoluteUri, StringComparison.Ordinal);

        // 256 bits as hex. Reused across transfers it would outlive the process
        // it was minted for.
        Assert.Equal(64, first.Secret.Length);
        Assert.All(first.Secret, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.NotEqual(first.Secret, second.Secret);
    }

    [Fact]
    public void Peers_and_seeds_reach_the_job_and_are_not_blanked_by_a_later_report()
    {
        var job = new DownloadJob { JobId = "job_1", ListingId = "lst_1", Title = "Doom" };

        DownloadQueue.ApplyTransfer(job, new DownloadProgress(1024, 4096, 512, TimeSpan.FromSeconds(1))
        {
            Peers = 27,
            Seeders = 4
        });

        Assert.Equal(27, job.Peers);
        Assert.Equal(4, job.Seeders);

        // The RPC poll and the progress stream run on their own schedules, so a
        // report can arrive between two answers. A count that blinked out every
        // other update would read as a fault rather than as no news.
        DownloadQueue.ApplyTransfer(job, new DownloadProgress(2048, 4096, 512, TimeSpan.FromSeconds(2)));

        Assert.Equal(27, job.Peers);
        Assert.Equal(4, job.Seeders);
        Assert.Equal(2048, job.BytesReceived);
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData(27, 4, "27 peers · 4 seeds")]
    [InlineData(8, null, "8 peers")]
    [InlineData(0, 0, "0 peers · 0 seeds")]
    public void The_table_shows_what_the_engine_actually_reported(int? peers, int? seeders, string expected)
    {
        // Zero is shown, not hidden: a torrent that has found nobody is precisely
        // the case someone is staring at the table to understand.
        var row = new DownloadItemViewModel(new DownloadJob
        {
            JobId = "job_1",
            ListingId = "lst_1",
            Title = "Doom",
            Peers = peers,
            Seeders = seeders
        });

        Assert.Equal(expected, row.PeersText);
    }

    /// <summary>A magnet that has not found the torrent metadata yet.</summary>
    /// <remarks>
    /// aria2 omits <c>bittorrent.info</c> until a peer supplies the info
    /// dictionary, and reports zero for the size and rate meanwhile. This body is
    /// the shape of the phase that used to be indistinguishable from a hang.
    /// </remarks>
    private const string MagnetResolving =
        """
        {"id":"gl","jsonrpc":"2.0","result":[{
          "gid":"7a1b2c3d4e5f6071",
          "completedLength":"0",
          "totalLength":"0",
          "downloadSpeed":"0",
          "connections":"0",
          "numSeeders":"0",
          "bittorrent":{"announceList":[["udp://tracker.example.test:6969/announce"]],"mode":"single"}
        }]}
        """;

    /// <summary>The same magnet once the metadata has arrived.</summary>
    private const string MagnetResolved =
        """
        {"id":"gl","jsonrpc":"2.0","result":[{
          "gid":"7a1b2c3d4e5f6071",
          "completedLength":"1048576",
          "totalLength":"343597383",
          "downloadSpeed":"524288",
          "connections":"12",
          "numSeeders":"3",
          "bittorrent":{"info":{"name":"doom.iso"},"mode":"single"}
        }]}
        """;

    [Fact]
    public async Task A_magnet_still_finding_the_metadata_says_so()
    {
        await using var server = await LoopbackRpcServer.StartAsync();
        server.ResponseBody = MagnetResolving;

        var status = await Client(server).TellActiveAsync();

        Assert.NotNull(status);
        Assert.True(status.IsTorrent);

        // The distinction the whole change rests on. Without it this status is
        // byte-for-byte what a dead transfer reports.
        Assert.True(status.MetadataPending);

        Assert.Null(status.TotalBytes);
        Assert.Equal(0, status.CompletedBytes);
        Assert.Equal(0, status.Connections);
    }

    [Fact]
    public async Task A_magnet_that_resolved_is_an_ordinary_torrent_again()
    {
        await using var server = await LoopbackRpcServer.StartAsync();
        server.ResponseBody = MagnetResolved;

        var status = await Client(server).TellActiveAsync();

        Assert.NotNull(status);
        Assert.True(status.IsTorrent);
        Assert.False(status.MetadataPending);
        Assert.Equal(343597383, status.TotalBytes);
        Assert.Equal(3, status.Seeders);
    }

    [Fact]
    public async Task An_http_transfer_is_not_mistaken_for_a_pending_torrent()
    {
        // No bittorrent member and no seeder count. Reporting MetadataPending
        // here would put "Finding peers" on a plain web download.
        await using var server = await LoopbackRpcServer.StartAsync();

        server.ResponseBody =
            """
            {"id":"gl","jsonrpc":"2.0","result":[{
              "gid":"a1","completedLength":"1024","totalLength":"4096",
              "downloadSpeed":"512","connections":"8"
            }]}
            """;

        var status = await Client(server).TellActiveAsync();

        Assert.NotNull(status);
        Assert.False(status.IsTorrent);
        Assert.False(status.MetadataPending);
    }

    [Fact]
    public void A_row_resolving_metadata_does_not_claim_to_be_downloading()
    {
        var row = new DownloadItemViewModel(new DownloadJob
        {
            JobId = "job_1",
            ListingId = "lst_1",
            Title = "Doom",
            Phase = DownloadPhase.Downloading,
            ResolvingMetadata = true,
            Peers = 0
        });

        Assert.Equal("Finding peers", row.PhaseText);

        // And the size slot explains the wait rather than showing nothing.
        Assert.Equal("Reading torrent details…", row.SizeText);
    }

    [Fact]
    public void A_stalled_row_shows_how_long_the_engine_will_keep_waiting()
    {
        // A wait with no visible end reads as a hang and gets cancelled; the
        // same wait with a countdown reads as patience.
        var row = new DownloadItemViewModel(new DownloadJob
        {
            JobId = "job_1",
            ListingId = "lst_1",
            Title = "Doom",
            Phase = DownloadPhase.Downloading,
            StalledFor = TimeSpan.FromMinutes(1),
            StallLimit = TimeSpan.FromMinutes(5)
        });

        Assert.Equal("waiting, 4m 0s left", row.EtaText);
    }

    [Fact]
    public void A_row_with_nothing_to_time_says_nothing()
    {
        // An ordinary download that simply has no estimate yet must not grow a
        // countdown out of nowhere.
        var row = new DownloadItemViewModel(new DownloadJob
        {
            JobId = "job_1",
            ListingId = "lst_1",
            Title = "Doom",
            Phase = DownloadPhase.Downloading
        });

        Assert.Equal(string.Empty, row.EtaText);
        Assert.Equal("Downloading", row.PhaseText);
    }

    [Fact]
    public void A_stalled_row_without_a_deadline_says_how_long_it_has_been_quiet()
    {
        // An HTTP transfer waits as long as the request allows rather than
        // abandoning a quiet connection on a deadline of its own, so there is no
        // countdown to show — but the silence itself is the useful fact, and this
        // slot used to be blank for exactly the transfer people were staring at.
        var row = new DownloadItemViewModel(new DownloadJob
        {
            JobId = "job_1",
            ListingId = "lst_1",
            Title = "Doom",
            Phase = DownloadPhase.Downloading,
            StalledFor = TimeSpan.FromSeconds(42)
        });

        Assert.Equal("stalled 42s", row.EtaText);
    }

    [Fact]
    public void A_job_object_contains_a_child_process()
    {
        // The point of the job is that the kernel enforces it, so the test has to
        // watch a real process die rather than check a flag.
        using var job = new ChildProcessJob();

        Assert.True(job.IsEnforced);

        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c pause",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(child);
        Assert.True(job.Assign(child!));
        Assert.False(child!.HasExited);

        // Closing the job is what a killed launcher's handle closing does.
        job.Dispose();

        Assert.True(child.WaitForExit(10_000), "the job did not kill its child");
    }

    [Fact]
    public void An_unassigned_process_outlives_the_job()
    {
        // The control for the test above: without assignment the child survives,
        // which is precisely the leak that stranded an aria2c downloading
        // invisibly for the best part of an hour.
        using var job = new ChildProcessJob();

        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c pause",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(child);

        job.Dispose();

        Assert.False(child!.WaitForExit(1_000));

        child.Kill(entireProcessTree: true);
        child.WaitForExit(10_000);
    }

    private static Aria2RpcClient Client(LoopbackRpcServer server) =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, new Aria2RpcSession(server.Port, "secret"));

    /// <summary>Reserves and releases a port, so nothing is listening on it.</summary>
    /// <returns>The port number.</returns>
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);

        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }
}
