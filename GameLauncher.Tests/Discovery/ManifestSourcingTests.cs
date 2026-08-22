using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Crawling;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Desktop.Services.Discovery.Sourcing;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Html;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the generic download-resolution framework and its three strategies.
/// </summary>
/// <remarks>
/// Resolution decides <em>which address</em>; the existing download stack does
/// everything after that. So these tests check what comes out of a page, not
/// what happens to it — no file is transferred anywhere in this class.
/// </remarks>
public sealed class ManifestSourcingTests
{
    private const string Sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string Sha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    /// <summary>Builds the adapter over a host's real store and hooks.</summary>
    /// <param name="host">The container under test.</param>
    /// <returns>The adapter.</returns>
    private static ManifestSourcingAdapter Adapter(TestAppHost host) =>
        new(
            host.Resolve<IFeedManifestStore>(),
            host.Resolve<IScriptHookRunner>(),
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            host.Resolve<IRobotsPolicy>(),
            NullLogger<ManifestSourcingAdapter>.Instance);

    /// <summary>A listing pointing at a page.</summary>
    /// <param name="page">The page it came from.</param>
    /// <returns>The listing.</returns>
    private static CatalogListing Listing(Uri? page = null) => new()
    {
        ListingId = "lst_1",
        Title = "Doom",
        SortTitle = "Doom",
        Year = 1993,
        MatchKey = TitleNormalizer.ComputeMatchKey("Doom", 1993),
        PrimarySourceKey = "test",
        ContentHash = "hash",
        IsDownloadable = true
    };

    /// <summary>Writes a manifest that resolves from a site's own pages.</summary>
    /// <param name="host">The container under test.</param>
    /// <param name="site">The site the manifest claims.</param>
    /// <param name="extra">Extra YAML for the sourcing section.</param>
    /// <param name="key">The manifest key.</param>
    /// <returns>A task that completes when the file is written.</returns>
    private static Task WriteManifestAsync(
        TestAppHost host,
        LoopbackSiteServer site,
        string extra = "",
        string key = "example")
    {
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        // allowPrivateHosts because the whole suite runs against 127.0.0.1,
        // which the address guard refuses by default and rightly so.
        var yaml =
            $"""
             key: {key}
             displayName: Example site
             match:
               hosts: ["{site.BaseAddress.Host}"]
             sourcing:
               enabled: true
               strategy: direct-link
               allowPrivateHosts: true
             {extra}

             """;

        return File.WriteAllTextAsync(Path.Combine(directory, $"{key}.yaml"), yaml);
    }

    /// <summary>A release page with one download on it.</summary>
    /// <param name="body">The markup inside main.</param>
    /// <returns>HTML.</returns>
    private static string Page(string body) =>
        $"<!doctype html><html><body><main>{body}</main></body></html>";

    [Fact]
    public async Task A_direct_link_is_read_off_the_page()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page("<a href='/files/doom.zip'>Download</a>"));

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        var download = Assert.Single(payload.Downloads);

        Assert.EndsWith("/files/doom.zip", download.Url, StringComparison.Ordinal);
        Assert.Equal("doom.zip", download.FileName);
        Assert.Equal(DownloadKind.Game, download.Kind);
    }

    [Fact]
    public async Task A_published_checksum_and_size_are_carried_through()
    {
        // The whole reason to read them: they reach the existing verification
        // path, so a crawled source verifies exactly like a curated one.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page(
            $"<ul><li><a href='/files/doom.zip'>doom.zip</a> — 1.8 GB — <code>{Sha256}</code></li></ul>"));

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        var download = Assert.Single(payload.Downloads);

        Assert.Equal(Sha256, download.Sha256);
        Assert.Equal(1_800_000_000, download.SizeBytes);

        // And the strongest digest is what the installer will be handed.
        Assert.Equal(Sha256, download.BestChecksum);
    }

    [Fact]
    public async Task Selectors_pin_down_where_each_value_lives()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page(
            "<a class='mirror' href='/files/wrong.zip'>Mirror</a>" +
            "<div class='release'><a class='dl' href='/files/doom.zip'>Get it</a>" +
            $"<span class='hash'>{Sha1}</span><span class='bytes'>700 MB</span></div>"));

        await WriteManifestAsync(host, site, """
  selectors:
    downloadLink: "a.dl"
    sha1: ".hash"
    size: ".bytes"
""");

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        var download = Assert.Single(payload.Downloads);

        Assert.EndsWith("/files/doom.zip", download.Url, StringComparison.Ordinal);
        Assert.Equal(Sha1, download.Sha1);
        Assert.Equal(700_000_000, download.SizeBytes);
    }

    [Fact]
    public async Task Several_mirrors_are_returned_in_the_order_the_page_listed_them()
    {
        // A page listing mirrors is stating a preference by its ordering, and
        // that is the only preference it states.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page(
            "<a href='/files/a.zip'>Mirror one</a>" +
            "<a href='/files/b.zip'>Mirror two</a>" +
            "<a href='/files/c.zip'>Mirror three</a>"));

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        Assert.Equal(3, payload.Downloads.Count);
        Assert.EndsWith("/files/a.zip", payload.Downloads[0].Url, StringComparison.Ordinal);
        Assert.Equal([0, 1, 2], payload.Downloads.Select(download => download.MirrorRank));
    }

    [Fact]
    public async Task An_address_on_another_host_is_refused_unless_it_was_allowed()
    {
        // A release page linking elsewhere is normal; following it blindly is
        // not. The host has to be named.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page(
            "<a href='https://mirror.elsewhere.test/doom.zip'>Mirror</a>" +
            "<a href='/files/doom.zip'>Direct</a>"));

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        var download = Assert.Single(payload.Downloads);

        Assert.EndsWith("/files/doom.zip", download.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_host_is_accepted()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        // Two loopback ports: the page's own, and a "mirror" host it names.
        await using var mirror = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page($"<a href='{mirror.Url("/files/doom.zip")}'>Mirror</a>"));

        await WriteManifestAsync(host, site, $"""
  allowedHosts: ["{mirror.BaseAddress.Host}"]
""");

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        Assert.Single(payload.Downloads);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.test/doom.zip")]
    public async Task Unsupported_schemes_never_reach_the_download_stack(string href)
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page($"<a href='{href}'>Download</a>"));

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        Assert.Empty(payload.Downloads);
        Assert.Equal(SourcingRefusal.NoPayload, payload.Refusal);
    }

    [Fact]
    public async Task A_magnet_is_refused_unless_the_manifest_allowed_one()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

        site.AddPage("/g/doom", Page($"<a href='{magnet}'>Torrent</a>"));

        await WriteManifestAsync(host, site);

        var refused = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        Assert.Empty(refused.Downloads);
    }

    [Fact]
    public async Task A_nonsense_checksum_is_dropped_rather_than_recorded()
    {
        // A field holding "unknown" would fail every transfer with a mismatch
        // that is really a typo on somebody's release page.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page(
            "<a href='/files/doom.zip'>doom.zip</a><code>checksum: unknown</code>"));

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        var download = Assert.Single(payload.Downloads);

        Assert.Null(download.BestChecksum);
    }

    [Fact]
    public async Task The_mapped_field_strategy_uses_what_the_catalogue_recorded()
    {
        // Nothing is fetched: the answer is already in hand.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        await WriteManifestAsync(host, site, """
  strategy: mapped-field
""");

        var listing = Listing();

        listing.Downloads =
        [
            new ListingDownload
            {
                ListingId = "lst_1",
                SourceKey = "example",
                Url = site.Url("/files/doom.zip").AbsoluteUri,
                FileName = "doom.zip",
                Sha256 = Sha256,
                MirrorRank = 0
            }
        ];

        site.ClearRequests();

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(listing, site.Url("/g/doom").AbsoluteUri);

        var download = Assert.Single(payload.Downloads);

        Assert.Equal(Sha256, download.Sha256);

        // The page was never read, because it did not need to be.
        Assert.Empty(site.Requests.Where(path => path.StartsWith("/g/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_recorded_address_is_still_vetted()
    {
        // Stored from a feed or a script, so the gate belongs here rather than
        // in a trust that the database was careful.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        await WriteManifestAsync(host, site, """
  strategy: mapped-field
""");

        var listing = Listing();

        listing.Downloads =
        [
            new ListingDownload
            {
                ListingId = "lst_1",
                SourceKey = "example",
                Url = "file:///C:/Windows/win.ini",
                MirrorRank = 0
            }
        ];

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(listing, site.Url("/g/doom").AbsoluteUri);

        Assert.Empty(payload.Downloads);
    }

    [Fact]
    public async Task A_manifest_carries_its_priority_onto_the_payload()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page("<a href='/files/doom.zip'>Download</a>"));

        await WriteManifestAsync(host, site, """
  priority: 250
""");

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        Assert.Equal(250, payload.Priority);
    }

    [Fact]
    public async Task The_higher_priority_manifest_claims_a_shared_host()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page("<a href='/files/doom.zip'>Download</a>"));

        // Named so that file-name order alone would pick the other one.
        await WriteManifestAsync(host, site, "  priority: 10\n", "a-low");
        await WriteManifestAsync(host, site, "  priority: 900\n", "z-high");

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        Assert.Equal(900, payload.Priority);
        Assert.Equal("z-high", Assert.Single(payload.Downloads).SourceKey);
    }

    [Fact]
    public async Task An_address_no_manifest_claims_is_declined_cleanly()
    {
        // Declining is not failing. The resolver must be free to try the next
        // adapter, and a refusal that looked like an error would stop it.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), "https://nobody-claims-this.test/g/x");

        Assert.Equal(SourcingRefusal.Unsupported, payload.Refusal);
        Assert.Empty(payload.Downloads);
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_is_reported_rather_than_thrown()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        await WriteManifestAsync(host, site);

        // No page registered at that address, so the site answers 404.
        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/missing").AbsoluteUri);

        Assert.Empty(payload.Downloads);
        Assert.Equal(SourcingRefusal.Unreachable, payload.Refusal);
        Assert.NotNull(payload.Explanation);
    }

    [Fact]
    public async Task Robots_denial_stops_resolution_too()
    {
        // Resolution is a network read like any other and gets no exemption.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.Robots = "User-agent: *\nDisallow: /g/";
        site.AddPage("/g/doom", Page("<a href='/files/doom.zip'>Download</a>"));

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        Assert.Empty(payload.Downloads);
    }

    [Fact]
    public async Task A_torrent_address_is_classified_as_one()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/g/doom", Page("<a href='/files/doom.torrent'>Torrent</a>"));

        await WriteManifestAsync(host, site);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        Assert.Equal(DownloadKind.Torrent, Assert.Single(payload.Downloads).Kind);
    }

    [Fact]
    public async Task The_shipped_resolver_example_answers_the_documented_contract()
    {
        // The file a person is told to copy, run by the code that will run it.
        // A contract documented in a comment and never executed is a contract
        // that drifts.
        if (PythonInterpreter.Command is not { } python)
        {
            return;
        }

        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "resolver.py"),
            Path.Combine(directory, "resolver.py"));

        await File.WriteAllTextAsync(
            Path.Combine(directory, "scripted.yaml"),
            $"""
             key: scripted
             displayName: Scripted site
             match:
               hosts: ["{site.BaseAddress.Host}"]
             sourcing:
               enabled: true
               strategy: external-script
               allowPrivateHosts: true
               script:
                 command: {python}
                 args: [resolver.py]
                 timeoutSeconds: 60
             """);

        var payload = await Adapter(host)
            .ExtractDownloadPayloadAsync(Listing(), site.Url("/g/doom").AbsoluteUri);

        var download = Assert.Single(payload.Downloads);

        // What the example's resolve() derives from the page it was handed.
        Assert.EndsWith("/g/doom/download.zip", download.Url, StringComparison.Ordinal);
        Assert.Equal("ZIP", download.Format);

        // No page was fetched: a resolver is asked instead of the site.
        Assert.Empty(site.Requests);
    }

    [Fact]
    public void A_manifest_declaring_external_script_must_name_one()
    {
        var manifest = new FeedManifest
        {
            Key = "scripted",
            Match = new FeedMatch { Hosts = ["example.test"] },
            Sourcing = new FeedSourcing { Strategy = SourcingStrategy.ExternalScript }
        };

        Assert.Contains(
            manifest.Validate(),
            problem => problem.Contains("sourcing.script", StringComparison.Ordinal));
    }

    [Fact]
    public void Lazy_resolution_is_the_default()
    {
        // Eager would cost a page fetch per game during every import, to answer
        // a question about the one game somebody eventually clicks.
        Assert.Equal(SourcingResolution.Lazy, new FeedSourcing().Resolution);
        Assert.Equal(SourcingStrategy.DirectLink, new FeedSourcing().Strategy);
        Assert.Equal(100, new FeedSourcing().Priority);
    }

    [Fact]
    public void A_candidate_becomes_a_download_row_without_a_parallel_model()
    {
        var candidate = new DownloadCandidate
        {
            Address = new Uri("https://example.test/doom.zip"),
            SourcePage = new Uri("https://example.test/g/doom"),
            SizeBytes = 42,
            Sha256 = Sha256
        };

        var download = candidate.ToDownload("lst_1", "example", 3);

        Assert.Equal("lst_1", download.ListingId);
        Assert.Equal("example", download.SourceKey);
        Assert.Equal(3, download.MirrorRank);
        Assert.Equal("doom.zip", download.FileName);
        Assert.Equal(Sha256, download.Sha256);
        Assert.Equal(42, download.SizeBytes);
    }

    [Theory]
    [InlineData("sha256:9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", 64)]
    [InlineData("  da39a3ee5e6b4b0d3255bfef95601890afd80709  ", 40)]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e  doom.zip", 32)]
    public void A_published_digest_is_read_in_the_forms_sites_print_it(string printed, int length)
    {
        var cleaned = HexDigest.Clean(printed);

        Assert.NotNull(cleaned);
        Assert.Equal(length, cleaned!.Length);
        Assert.Equal(cleaned, cleaned.ToLowerInvariant());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("see below")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_value_that_is_not_a_digest_is_refused(string printed) =>
        Assert.Null(HexDigest.Clean(printed));
}
