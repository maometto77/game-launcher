using System.Text;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Install;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using GameLauncher.Desktop.Services.Discovery.Sourcing;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the pluggable sourcing engine: manifests read from disk, payloads in
/// four shapes, and the addresses they produce reaching the download stack.
/// </summary>
/// <remarks>
/// The reading and mapping halves are pure, so they are tested against captured
/// payloads. Only the tests that genuinely need a socket or a child process
/// start one.
/// </remarks>
public sealed class ScriptableSourcingTests
{
    /// <summary>A syntactically valid SHA-1, so the digest filter accepts it.</summary>
    private const string Sha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    [Fact]
    public void A_json_payload_maps_to_downloads()
    {
        var payload = FeedReader.Read(
            $$"""
              { "files": [ { "name": "doom.zip",
                             "url": "https://a.test/doom.zip",
                             "size": 4096,
                             "sha1": "{{Sha1}}" } ] }
              """,
            FeedFormat.Json);

        var downloads = FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1");

        var download = Assert.Single(downloads);

        Assert.Equal("https://a.test/doom.zip", download.Url);
        Assert.Equal("doom.zip", download.FileName);
        Assert.Equal(4096, download.SizeBytes);
        Assert.Equal(Sha1, download.Sha1);
        Assert.Equal(DownloadKind.Game, download.Kind);
    }

    [Fact]
    public void A_yaml_payload_maps_through_the_same_rules()
    {
        // The same manifest paths against a different format. Normalising at the
        // parse boundary is what makes that true, and this is the test that says
        // so.
        var payload = FeedReader.Read(
            $"""
             files:
               - name: doom.zip
                 url: https://a.test/doom.zip
                 size: 4096
                 sha1: {Sha1}
             """,
            FeedFormat.Yaml);

        var download = Assert.Single(FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1"));

        Assert.Equal("https://a.test/doom.zip", download.Url);
        Assert.Equal(4096, download.SizeBytes);
    }

    [Fact]
    public void An_rss_enclosure_supplies_the_address()
    {
        var payload = FeedReader.Read(
            """
            <rss version="2.0"><channel>
              <title>Releases</title>
              <item>
                <title>Doom</title>
                <enclosure url="https://a.test/doom.zip" length="4096" type="application/zip" />
              </item>
            </channel></rss>
            """,
            FeedFormat.Feed);

        var manifest = Manifest();
        manifest.Format = FeedFormat.Feed;
        manifest.Items = "channel.item";
        manifest.Map = new FeedDownloadMap { Url = "enclosure.@url", SizeBytes = "enclosure.@length", Title = "title" };

        var download = Assert.Single(FeedDownloadMapper.Map(payload, manifest, "lst_1"));

        Assert.Equal("https://a.test/doom.zip", download.Url);
        Assert.Equal(4096, download.SizeBytes);
    }

    [Fact]
    public void An_atom_entry_is_read_without_naming_its_namespace()
    {
        // Nobody editing a YAML file by hand should have to write an XML
        // namespace, so element names are taken without one.
        var payload = FeedReader.Read(
            """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <title type="text">Doom</title>
                <link rel="enclosure" href="https://a.test/doom.zip" />
              </entry>
            </feed>
            """,
            FeedFormat.Feed);

        var manifest = Manifest();
        manifest.Format = FeedFormat.Feed;
        manifest.Items = "entry";
        manifest.Map = new FeedDownloadMap { Url = "link.@href", Title = "title" };

        Assert.Equal("https://a.test/doom.zip", Assert.Single(FeedDownloadMapper.Map(payload, manifest, "lst_1")).Url);

        // An element carrying both an attribute and text yields the text.
        Assert.Equal("Doom", payload.Select("entry").String("title"));
    }

    [Fact]
    public void A_publisher_who_omits_the_array_is_read_the_same_way()
    {
        // Feeds routinely publish an object when there is one item and an array
        // when there are several. That difference must not reach the manifest.
        var payload = FeedReader.Read(
            """{ "files": { "url": "https://a.test/doom.zip" } }""", FeedFormat.Json);

        Assert.Single(FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1"));
    }

    [Fact]
    public void A_path_that_misses_yields_nothing_rather_than_throwing()
    {
        var payload = FeedReader.Read("""{ "files": [] }""", FeedFormat.Json);

        Assert.Null(payload.String("nothing.here.at.all"));
        Assert.Null(payload.Int64("files.0.size"));
        Assert.Empty(FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1"));
    }

    [Fact]
    public void A_malformed_payload_is_one_exception_type_whatever_the_format()
    {
        Assert.Throws<FormatException>(() => FeedReader.Read("{ not json", FeedFormat.Json));
        Assert.Throws<FormatException>(() => FeedReader.Read("<rss><unclosed>", FeedFormat.Feed));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", DownloadKind.Torrent)]
    [InlineData("https://a.test/doom_archive.torrent", DownloadKind.Torrent)]
    [InlineData("https://a.test/doom.zip", DownloadKind.Game)]
    public void Torrent_payloads_are_recognised(string url, DownloadKind expected)
    {
        // The same two rules the download service applies when choosing a
        // transport, so a row classified here reaches aria2 for exactly the
        // addresses aria2 is needed for.
        var payload = FeedReader.Read($$"""{ "files": [ { "url": "{{url}}" } ] }""", FeedFormat.Json);

        Assert.Equal(expected, Assert.Single(FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1")).Kind);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path.zip")]
    [InlineData("")]
    public void An_address_the_download_stack_cannot_fetch_is_dropped(string url)
    {
        // A feed that could name a local path would turn "add this manifest"
        // into "copy anything on this machine".
        var payload = FeedReader.Read($$"""{ "files": [ { "url": "{{url}}" } ] }""", FeedFormat.Json);

        Assert.Empty(FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1"));
    }

    [Fact]
    public void A_checksum_that_is_not_a_digest_is_discarded()
    {
        // Worse than an absent one: the download service would compare against
        // it and fail every transfer over what is really a feed typo.
        var payload = FeedReader.Read(
            """{ "files": [ { "url": "https://a.test/doom.zip", "sha1": "not computed" } ] }""",
            FeedFormat.Json);

        Assert.Null(Assert.Single(FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1")).Sha1);
    }

    [Fact]
    public void A_feed_and_a_page_read_a_printed_digest_the_same_way()
    {
        // One rule, in one place, for every source that can carry a checksum:
        // they all feed the same verification, so disagreeing about what counts
        // as a digest would mean the same published value verifying a download
        // from one source and failing it from another.
        const string Printed = "sha1:DA39A3EE5E6B4B0D3255BFEF95601890AFD80709  doom.zip";

        var payload = FeedReader.Read(
            $$"""{ "files": [ { "url": "https://a.test/doom.zip", "sha1": "{{Printed}}" } ] }""",
            FeedFormat.Json);

        var mapped = Assert.Single(FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1"));

        // An algorithm prefix, mixed case and the trailing file name sha1sum
        // prints are all unwrapped rather than being grounds for discarding a
        // perfectly good checksum.
        Assert.Equal(HexDigest.Clean(Printed), mapped.Sha1);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", mapped.Sha1);
    }

    [Fact]
    public void Feed_order_becomes_mirror_order()
    {
        var payload = FeedReader.Read(
            """
            { "files": [ { "url": "https://fast.test/doom.zip" },
                         { "url": "https://slow.test/doom.zip" } ] }
            """,
            FeedFormat.Json);

        var downloads = FeedDownloadMapper.Map(payload, JsonManifest(), "lst_1");

        // A publisher who lists a fast mirror first meant it.
        Assert.Equal("https://fast.test/doom.zip", downloads[0].Url);
        Assert.Equal(0, downloads[0].MirrorRank);
        Assert.Equal(1, downloads[1].MirrorRank);
    }

    [Fact]
    public async Task Manifests_are_read_from_both_yaml_and_json()
    {
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(Path.Combine(directory, "a-nas.yaml"), YamlManifest("nas", "nas.test"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "b-mirror.json"),
            """
            {
              // Comments are tolerated: these are files people edit by hand.
              "key": "mirror",
              "displayName": "Mirror",
              "match": { "hosts": ["mirror.test"] },
              "request": { "url": "feed.json" },
              "format": "json",
              "items": "files",
              "map": { "url": "url" },
            }
            """);

        var manifests = await host.Resolve<IFeedManifestStore>().GetAsync();

        Assert.Equal(["nas", "mirror"], manifests.Select(manifest => manifest.Key));
        Assert.Equal(FeedFormat.Json, manifests[1].Format);
    }

    [Fact]
    public async Task A_broken_manifest_is_skipped_and_the_rest_still_load()
    {
        // These files are written by hand, so one with a typo in it is the
        // expected case. Taking the whole engine down over it would be a poor
        // trade.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(Path.Combine(directory, "a-broken.yaml"), "key: broken\nmatch: {}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "b-nonsense.yaml"), "\t: not: valid: yaml: [");
        await File.WriteAllTextAsync(Path.Combine(directory, "c-good.yaml"), YamlManifest("good", "good.test"));

        var manifests = await host.Resolve<IFeedManifestStore>().GetAsync();

        Assert.Equal("good", Assert.Single(manifests).Key);
    }

    [Fact]
    public async Task Two_manifests_claiming_one_key_is_refused_rather_than_resolved_by_luck()
    {
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(Path.Combine(directory, "a.yaml"), YamlManifest("same", "one.test"));
        await File.WriteAllTextAsync(Path.Combine(directory, "b.yaml"), YamlManifest("same", "two.test"));

        var manifests = await host.Resolve<IFeedManifestStore>().GetAsync();

        // Which one won would otherwise depend on directory order.
        Assert.Equal("one.test", Assert.Single(manifests).Match.Hosts[0]);
    }

    [Fact]
    public async Task A_disabled_manifest_is_left_alone()
    {
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "off.yaml"), YamlManifest("off", "off.test") + "\nenabled: false\n");

        Assert.Empty(await host.Resolve<IFeedManifestStore>().GetAsync());
    }

    [Fact]
    public async Task Adding_a_manifest_is_noticed_without_a_restart()
    {
        using var host = new TestAppHost();
        var store = host.Resolve<IFeedManifestStore>();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        Assert.Empty(await store.GetAsync());

        await File.WriteAllTextAsync(Path.Combine(directory, "new.yaml"), YamlManifest("new", "new.test"));

        // Stamped from the directory's own write time, which changes when a file
        // is added to it.
        Assert.Single(await store.GetAsync());
    }

    [Fact]
    public async Task Every_shipped_example_manifest_actually_loads()
    {
        // The files a user is told to copy, loaded by the code that will read
        // them. Documentation that has drifted from the implementation is worse
        // than none, and this is the only way to notice it has.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        var examples = Directory.EnumerateFiles(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples")).ToArray();

        Assert.NotEmpty(examples);

        foreach (var example in examples)
        {
            File.Copy(example, Path.Combine(directory, Path.GetFileName(example)));
        }

        var manifests = await host.Resolve<IFeedManifestStore>().GetAsync();

        Assert.Equal(
            ["archive-org-library", "local-catalog", "local-shelf", "releases-rss", "zenodo"],
            manifests.Select(manifest => Path.GetFileNameWithoutExtension(manifest.SourcePath)).Order());

        // shelf.json is a payload sitting beside the manifest that reads it, not
        // a broken manifest, so it is passed over rather than complained about.
        Assert.DoesNotContain(manifests, manifest => manifest.SourcePath.EndsWith("shelf.json", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_leads_the_built_ins_unless_it_says_otherwise()
    {
        // Someone who wrote a manifest for a host this launcher already handles
        // meant it to be used. Having to say so twice — once by writing the file
        // and again by numbering it — would be a poor default.
        Assert.Equal(100, Manifest().Priority);
        Assert.Equal(100, new FeedManifest().Priority);
    }

    [Fact]
    public async Task A_manifests_priority_reaches_the_payload_that_ranks_it()
    {
        // One adapter serves every feed in the folder and they do not agree
        // about where they belong, so the number has to travel with the answer
        // rather than sit on the adapter.
        await using var server = await LoopbackFileServer.StartAsync();

        server.AddFile("doom.zip", Archive());

        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "slow.yaml"),
            YamlManifest("slow", "local.test") + "priority: -10\n");

        await File.WriteAllTextAsync(
            Path.Combine(directory, "feed.json"),
            $$"""{ "files": [ { "url": "{{server.FileUrl("doom.zip").AbsoluteUri}}", "name": "doom.zip" } ] }""");

        var payload = await Adapter(host).ExtractDownloadPayloadAsync(
            Listing(), "https://local.test/games/doom");

        Assert.True(payload.HasDownloads);
        Assert.Equal(-10, payload.Priority);
    }

    [Fact]
    public async Task The_higher_priority_manifest_claims_a_shared_host()
    {
        // Two manifests overlapping is how someone stages a replacement. The
        // number is how they say which one they meant.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        // Named so that file-name order would pick the *other* one, which is
        // what makes this a test of the priority rather than of the ordering it
        // falls back on.
        await File.WriteAllTextAsync(
            Path.Combine(directory, "a-old.yaml"),
            YamlManifest("old", "local.test") + "priority: 10\n");

        await File.WriteAllTextAsync(
            Path.Combine(directory, "z-new.yaml"),
            YamlManifest("new", "local.test") + "priority: 200\n");

        await File.WriteAllTextAsync(
            Path.Combine(directory, "feed.json"),
            """{ "files": [ { "url": "https://cdn.test/doom.zip", "name": "doom.zip" } ] }""");

        var payload = await Adapter(host).ExtractDownloadPayloadAsync(
            Listing(), "https://local.test/games/doom");

        Assert.Equal(200, payload.Priority);
        Assert.Equal("new", Assert.Single(payload.Downloads).SourceKey);
    }

    [Fact]
    public async Task The_archive_example_is_valid_even_though_it_ships_disabled()
    {
        // It ships disabled because it overrides a working built-in adapter, so
        // the loader passes over it and the "every example loads" test above
        // never sees it. Without this, the one example a user is most likely to
        // switch on is the one nothing checks.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        var text = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "archive-org-feed.yaml"));

        Assert.Contains("enabled: false", text, StringComparison.Ordinal);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "archive-org-feed.yaml"),
            text.Replace("enabled: false", "enabled: true", StringComparison.Ordinal));

        var manifest = Assert.Single(await host.Resolve<IFeedManifestStore>().GetAsync());

        Assert.Equal("archive-org-feed", manifest.Key);
        Assert.Empty(manifest.Validate());

        // It claims Archive item pages and nothing else on that host.
        Assert.Contains("archive.org", manifest.Match.Hosts);
        Assert.Contains("/details/", manifest.Match.PathContains);

        // The script it names has to be the one that actually ships beside it.
        Assert.NotNull(manifest.Transform);
        Assert.Contains("template-scraper.py", manifest.Transform!.Args);

        Assert.True(File.Exists(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "template-scraper.py")));
    }

    [Fact]
    public async Task The_archive_example_maps_what_its_scraper_emits()
    {
        // The manifest's paths and the scraper's field names are two halves of
        // one contract written in two languages. This is what notices when only
        // one of them is edited.
        var payload = FeedReader.Read(
            """
            { "results": [
                { "title": "Doom",
                  "file_name": "Doom_1993.zip",
                  "download_url": "https://archive.org/download/msdos_Doom_1993/Doom_1993.zip",
                  "sha1": "da39a3ee5e6b4b0d3255bfef95601890afd80709",
                  "md5": "cccccccccccccccccccccccccccccccc",
                  "checksum": "sha1:da39a3ee5e6b4b0d3255bfef95601890afd80709",
                  "size_bytes": 2359527,
                  "format": "ZIP" },
                { "title": "Doom",
                  "file_name": "msdos_Doom_1993_archive.torrent",
                  "download_url":
                    "https://archive.org/download/msdos_Doom_1993/msdos_Doom_1993_archive.torrent",
                  "sha1": null, "md5": null, "checksum": null,
                  "size_bytes": null, "format": "Torrent" } ] }
            """,
            FeedFormat.Json);

        var manifest = await LoadArchiveExampleAsync();
        var downloads = FeedDownloadMapper.Map(payload, manifest, "lst_1");

        Assert.Equal(2, downloads.Count);

        Assert.Equal("Doom_1993.zip", downloads[0].FileName);
        Assert.Equal(2359527, downloads[0].SizeBytes);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", downloads[0].Sha1);
        Assert.Equal(DownloadKind.Game, downloads[0].Kind);

        // Last, and classified as a torrent by its extension alone — the same
        // rule the download service uses when it picks a transport.
        Assert.Equal(DownloadKind.Torrent, downloads[1].Kind);
        Assert.True(downloads[0].MirrorRank < downloads[1].MirrorRank);

        // The digests of the .torrent itself describe the pointer, not what it
        // delivers, so the scraper drops them rather than have the download
        // service verify the wrong thing.
        Assert.Null(downloads[1].Sha1);
        Assert.Null(downloads[1].SizeBytes);
    }

    [Fact]
    public async Task The_archive_scraper_turns_a_real_item_into_addresses()
    {
        // The whole path, as it runs on a user's machine: the captured payload
        // through the real hook runner into the real mapper.
        if (PythonInterpreter.Command is not { } python)
        {
            // No Python on this machine, so there is nothing to exercise. The
            // manifest and mapping halves are covered above without it.
            return;
        }

        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "template-scraper.py"),
            Path.Combine(directory, "template-scraper.py"));

        var output = await host.Resolve<IScriptHookRunner>().RunAsync(
            new FeedTransform { Command = python, Args = ["template-scraper.py"], TimeoutSeconds = 60 },
            Fixture("archive-downloadable-item.json"),
            directory);

        var manifest = await LoadArchiveExampleAsync();
        var downloads = FeedDownloadMapper.Map(FeedReader.Read(output, FeedFormat.Json), manifest, "lst_1");

        Assert.Equal(2, downloads.Count);

        Assert.Equal(
            "https://archive.org/download/msdos_Doom_1993/Doom_1993.zip",
            downloads[0].Url);

        Assert.Equal("dddddddddddddddddddddddddddddddddddddddd", downloads[0].Sha1);
        Assert.Equal(DownloadKind.Torrent, downloads[1].Kind);

        // Screenshots, the item tile and the _files.xml are all originals too.
        // Only what could actually be installed is offered.
        Assert.DoesNotContain(downloads, download =>
            download.FileName?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true ||
            download.FileName?.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task The_archive_scraper_finds_a_torrent_published_as_metadata()
    {
        // A real item marks its .torrent 'metadata', not 'original', so a
        // scraper that looks for it among the originals never finds one.
        // archive-downloadable-item.json happens to say 'original', which is
        // why this case needs a payload of its own rather than that fixture.
        if (PythonInterpreter.Command is not { } python)
        {
            return;
        }

        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "template-scraper.py"),
            Path.Combine(directory, "template-scraper.py"));

        var output = await host.Resolve<IScriptHookRunner>().RunAsync(
            new FeedTransform { Command = python, Args = ["template-scraper.py"], TimeoutSeconds = 60 },
            """
            { "metadata": { "identifier": "alice", "title": "Alice" },
              "files": [
                { "name": "Alice.zip", "source": "original", "format": "ZIP",
                  "size": "989711238",
                  "sha1": "62739d2989cda3facb92304251ccb4e60735dcdd" },
                { "name": "alice_archive.torrent", "source": "metadata",
                  "format": "Archive BitTorrent", "size": "3421" } ] }
            """,
            directory);

        var manifest = await LoadArchiveExampleAsync();
        var downloads = FeedDownloadMapper.Map(FeedReader.Read(output, FeedFormat.Json), manifest, "lst_1");

        Assert.Equal(2, downloads.Count);

        var torrent = Assert.Single(downloads, download => download.Kind == DownloadKind.Torrent);

        Assert.EndsWith("alice_archive.torrent", torrent.Url, StringComparison.Ordinal);

        // Ranked last, because it only works when aria2c is installed.
        Assert.Equal(downloads[^1].Url, torrent.Url);
    }

    [Fact]
    public async Task The_archive_scraper_refuses_a_restricted_item()
    {
        if (PythonInterpreter.Command is not { } python)
        {
            return;
        }

        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "template-scraper.py"),
            Path.Combine(directory, "template-scraper.py"));

        var output = await host.Resolve<IScriptHookRunner>().RunAsync(
            new FeedTransform { Command = python, Args = ["template-scraper.py"], TimeoutSeconds = 60 },
            Fixture("archive-restricted-item.json"),
            directory);

        var manifest = await LoadArchiveExampleAsync();

        // An item the Archive shows but will not release. Its addresses answer
        // 403, so offering them would turn a clear explanation into a failure.
        Assert.Empty(FeedDownloadMapper.Map(FeedReader.Read(output, FeedFormat.Json), manifest, "lst_1"));
    }

    [Fact]
    public async Task The_zenodo_example_maps_that_repositorys_real_payload_shape()
    {
        // Captured from https://zenodo.org/api/records/21012370/files on
        // 2026-08-09 — the one path Zenodo's robots.txt allows back in after
        // disallowing /api wholesale.
        var payload = FeedReader.Read(
            """
            { "entries": [ {
                "key": "example.zip",
                "size": 546141,
                "checksum": "md5:984da7f784c11035f4115eea046f2438",
                "links": {
                  "self": "https://zenodo.org/api/records/21012370/files/example.zip",
                  "content": "https://zenodo.org/api/records/21012370/files/example.zip/content"
                } } ] }
            """,
            FeedFormat.Json);

        var manifest = await LoadExampleAsync("zenodo.yaml");
        var download = Assert.Single(FeedDownloadMapper.Map(payload, manifest, "lst_1"));

        Assert.EndsWith("/content", download.Url, StringComparison.Ordinal);
        Assert.Equal("example.zip", download.FileName);
        Assert.Equal(546141, download.SizeBytes);

        // The 'md5:' prefix is stripped rather than the checksum discarded.
        Assert.Equal("984da7f784c11035f4115eea046f2438", download.Md5);
    }

    [Fact]
    public async Task The_rss_example_maps_a_real_enclosure()
    {
        var payload = FeedReader.Read(
            """
            <rss version="2.0"><channel>
              <item>
                <title>Doom</title>
                <enclosure url="https://releases.example.org/doom.zip" length="2359527" />
              </item>
            </channel></rss>
            """,
            FeedFormat.Feed);

        var manifest = await LoadExampleAsync("releases-rss.yaml");
        var download = Assert.Single(FeedDownloadMapper.Map(payload, manifest, "lst_1"));

        Assert.Equal("https://releases.example.org/doom.zip", download.Url);
        Assert.Equal(2359527, download.SizeBytes);
    }

    [Fact]
    public async Task The_shelf_example_and_its_payload_agree()
    {
        var manifest = await LoadExampleAsync("local-shelf.yaml");

        var payload = FeedReader.Read(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "shelf.json")),
            FeedFormat.Json);

        var downloads = FeedDownloadMapper.Map(payload, manifest, "lst_1");

        Assert.Equal(2, downloads.Count);
        Assert.Equal("example-game.zip", downloads[0].FileName);

        // The second entry is a magnet URI, which the example claims is
        // recognised automatically. It has to be.
        Assert.Equal(DownloadKind.Torrent, downloads[1].Kind);
    }

    [Fact]
    public async Task A_local_feed_file_needs_no_server_at_all()
    {
        // A manifest and a JSON file beside it: a purely local catalogue, which
        // is the case a home library actually has.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(Path.Combine(directory, "local.yaml"), YamlManifest("local", "local.test"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "feed.json"),
            """{ "files": [ { "url": "https://local.test/doom.zip", "name": "doom.zip" } ] }""");

        var payload = await Adapter(host).ExtractDownloadPayloadAsync(
            Listing(), "https://local.test/games/doom");

        Assert.True(payload.HasDownloads);
        Assert.Equal("https://local.test/doom.zip", payload.Downloads[0].Url);
        Assert.Equal("local", payload.Downloads[0].SourceKey);
    }

    [Fact]
    public async Task A_feed_file_outside_the_adapter_directory_is_refused()
    {
        // Without this check a manifest could name '../../../../Windows/win.ini'
        // and have the launcher read it — local file disclosure dressed as a feed.
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "escape.yaml"),
            YamlManifest("escape", "escape.test").Replace("feed.json", "../../../secrets.json"));

        var payload = await Adapter(host).ExtractDownloadPayloadAsync(
            Listing(), "https://escape.test/games/doom");

        Assert.False(payload.HasDownloads);
        Assert.Contains("outside the adapter directory", payload.Explanation);
    }

    [Fact]
    public async Task A_feed_served_over_http_supplies_downloads()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        server.AddFile("feed.json", Encoding.UTF8.GetBytes(
            """{ "files": [ { "url": "https://cdn.test/doom.zip", "name": "doom.zip" } ] }"""));

        using var host = new TestAppHost();
        var feed = server.FileUrl("feed.json");

        await File.WriteAllTextAsync(
            Path.Combine(host.Resolve<IAppPaths>().AdapterDirectory, "http.yaml"),
            $"""
             key: http
             displayName: Over HTTP
             match:
               hosts: [{feed.Host}]
             request:
               url: {feed.AbsoluteUri}
             format: json
             items: files
             map:
               url: url
               fileName: name
             """);

        var payload = await Adapter(host).ExtractDownloadPayloadAsync(
            Listing(), $"https://{feed.Host}:{feed.Port}/games/doom");

        Assert.True(payload.HasDownloads);
        Assert.Equal("https://cdn.test/doom.zip", payload.Downloads[0].Url);
    }

    [Fact]
    public async Task A_feed_the_site_disallows_is_not_fetched()
    {
        // A manifest is a user's instruction to this launcher, not a dispensation
        // from the site's. The extension point obeys the rule the built-in
        // adapters obey, or it is a way around it.
        using var host = new TestAppHost();

        await File.WriteAllTextAsync(
            Path.Combine(host.Resolve<IAppPaths>().AdapterDirectory, "blocked.yaml"),
            YamlManifest("blocked", "blocked.test").Replace("url: feed.json", "url: https://blocked.test/feed.json"));

        var payload = await Adapter(host, robotsAllow: false).ExtractDownloadPayloadAsync(
            Listing(), "https://blocked.test/games/doom");

        Assert.False(payload.HasDownloads);
        Assert.Equal(SourcingRefusal.DisallowedByRobots, payload.Refusal);
    }

    [Fact]
    public async Task An_address_no_manifest_claims_is_unsupported()
    {
        using var host = new TestAppHost();

        await File.WriteAllTextAsync(
            Path.Combine(host.Resolve<IAppPaths>().AdapterDirectory, "one.yaml"),
            YamlManifest("one", "claimed.test"));

        var adapter = Adapter(host);

        // Warmed, so CanHandle answers from what is loaded rather than hopefully.
        await adapter.ExtractDownloadPayloadAsync(Listing(), "https://claimed.test/g");

        Assert.False(adapter.CanHandle("https://unclaimed.test/g"));
        Assert.True(adapter.CanHandle("https://files.claimed.test/g"));

        Assert.Equal(
            SourcingRefusal.Unsupported,
            (await adapter.ExtractDownloadPayloadAsync(Listing(), "https://unclaimed.test/g")).Refusal);
    }

    [Fact]
    public async Task A_transform_hook_supplies_the_payload_that_gets_mapped()
    {
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        // A program the user already has, named in a file the user wrote. The
        // launcher hosts no interpreter, so this is the whole contract.
        await File.WriteAllTextAsync(
            Path.Combine(directory, "hook.cmd"),
            "@echo off\r\necho {\"files\":[{\"url\":\"https://hooked.test/doom.zip\"}]}\r\n");

        await File.WriteAllTextAsync(Path.Combine(directory, "feed.json"), """{ "files": [] }""");

        await File.WriteAllTextAsync(
            Path.Combine(directory, "hooked.yaml"),
            YamlManifest("hooked", "hooked.test") +
            "transform:\n  command: cmd.exe\n  args: ['/c', 'hook.cmd']\n");

        var payload = await Adapter(host).ExtractDownloadPayloadAsync(
            Listing(), "https://hooked.test/games/doom");

        // The feed itself described nothing; everything here came from the hook.
        Assert.True(payload.HasDownloads);
        Assert.Equal("https://hooked.test/doom.zip", payload.Downloads[0].Url);
    }

    [Fact]
    public async Task A_hook_reads_the_payload_it_is_given()
    {
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        // Reads one line from standard input and writes it back: an identity
        // filter, which is exactly enough to prove the pipe runs both ways.
        await File.WriteAllTextAsync(
            Path.Combine(directory, "echo.cmd"), "@echo off\r\nset /p input=\r\necho %input%\r\n");

        var output = await host.Resolve<IScriptHookRunner>().RunAsync(
            new FeedTransform { Command = "cmd.exe", Args = ["/c", "echo.cmd"] },
            "hello-from-the-launcher",
            directory);

        Assert.Contains("hello-from-the-launcher", output);
    }

    [Fact]
    public async Task A_hook_that_fails_is_reported_rather_than_ignored()
    {
        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(
            Path.Combine(directory, "bad.cmd"), "@echo off\r\necho something went wrong 1>&2\r\nexit /b 3\r\n");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Resolve<IScriptHookRunner>().RunAsync(
                new FeedTransform { Command = "cmd.exe", Args = ["/c", "bad.cmd"] }, "{}", directory));

        Assert.Contains("exited with code 3", failure.Message);
    }

    [Fact]
    public async Task A_hook_naming_a_program_that_does_not_exist_says_so()
    {
        using var host = new TestAppHost();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Resolve<IScriptHookRunner>().RunAsync(
                new FeedTransform { Command = "no-such-program-anywhere.exe" },
                "{}",
                host.Resolve<IAppPaths>().AdapterDirectory));

        Assert.Contains("could not be started", failure.Message);
    }

    [Fact]
    public async Task A_feed_supplied_address_installs_through_the_existing_download_path()
    {
        // The end of the chain: a manifest on disk, an address it produced, and
        // the same install path everything else uses. Nothing about the queue or
        // the download service knows a custom feed was involved.
        await using var server = await LoopbackFileServer.StartAsync();

        server.AddFile("doom.zip", Archive());

        using var host = new TestAppHost();
        var directory = host.Resolve<IAppPaths>().AdapterDirectory;

        await File.WriteAllTextAsync(Path.Combine(directory, "local.yaml"), YamlManifest("local", "local.test"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "feed.json"),
            $$"""{ "files": [ { "url": "{{server.FileUrl("doom.zip").AbsoluteUri}}", "name": "doom.zip" } ] }""");

        var repository = host.Resolve<ICatalogListingRepository>();
        var listing = Listing();

        // The resolver reads the source's own normalised record rather than the
        // row's columns, so the observation has to be a real one.
        var observation = new SourceListing
        {
            SourceKey = "local",
            SourceItemId = "doom",
            SourceUrl = new Uri("https://local.test/games/doom"),
            Title = listing.Title,
            Year = listing.Year,
            RawPayload = "{}"
        };

        await repository.UpsertManyAsync([listing]);
        await repository.UpsertSourceAsync(new ListingSourceRecord
        {
            ListingId = listing.ListingId,
            SourceKey = observation.SourceKey,
            SourceItemId = observation.SourceItemId,
            SourceUrl = observation.SourceUrl.AbsoluteUri,
            NormalizedJson = System.Text.Json.JsonSerializer.Serialize(
                observation, new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)),
            FetchedAt = DateTimeOffset.Now,
            SourceContentHash = "hash"
        });

        var resolver = new DownloadSourceResolver(
            [Adapter(host)],
            repository,
            host.Resolve<IListingNormalizer>(),
            NullLogger<DownloadSourceResolver>.Instance);

        var payload = await resolver.ResolveAsync(listing);

        Assert.True(payload.HasDownloads);

        var install = await host.Resolve<IListingInstallService>().PrepareAsync(listing.ListingId);

        Assert.True(install.Succeeded);
    }

    /// <summary>Builds the adapter over a host's real manifest store and hook runner.</summary>
    /// <param name="host">The container under test.</param>
    /// <param name="robotsAllow">What the robots policy should answer.</param>
    /// <returns>The adapter.</returns>
    /// <remarks>
    /// The robots policy is the one thing substituted: the real one would fetch
    /// <c>robots.txt</c> from a host that does not exist, and a test should not
    /// depend on how a DNS failure is reported.
    /// </remarks>
    private static ScriptableSourcingAdapter Adapter(TestAppHost host, bool robotsAllow = true) =>
        new(
            host.Resolve<IFeedManifestStore>(),
            host.Resolve<IScriptHookRunner>(),
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            new FixedRobots(robotsAllow),
            NullLogger<ScriptableSourcingAdapter>.Instance);

    /// <summary>Reads a captured payload from the fixtures folder.</summary>
    /// <param name="name">File name of the fixture.</param>
    /// <returns>Its contents.</returns>
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Discovery", "Fixtures", name));

    /// <summary>
    /// Loads the Archive example, which ships disabled, as an enabled manifest.
    /// </summary>
    /// <returns>The loaded manifest.</returns>
    private static async Task<FeedManifest> LoadArchiveExampleAsync()
    {
        using var host = new TestAppHost();

        var text = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", "archive-org-feed.yaml"));

        await File.WriteAllTextAsync(
            Path.Combine(host.Resolve<IAppPaths>().AdapterDirectory, "archive-org-feed.yaml"),
            text.Replace("enabled: false", "enabled: true", StringComparison.Ordinal));

        return Assert.Single(await host.Resolve<IFeedManifestStore>().GetAsync());
    }

    /// <summary>Loads one shipped example manifest through the real store.</summary>
    /// <param name="name">File name of the example.</param>
    /// <returns>The loaded manifest.</returns>
    private static async Task<FeedManifest> LoadExampleAsync(string name)
    {
        using var host = new TestAppHost();

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "AdapterExamples", name),
            Path.Combine(host.Resolve<IAppPaths>().AdapterDirectory, name));

        return Assert.Single(await host.Resolve<IFeedManifestStore>().GetAsync());
    }

    /// <summary>A manifest whose paths match the JSON payloads used above.</summary>
    /// <returns>The manifest.</returns>
    private static FeedManifest JsonManifest()
    {
        var manifest = Manifest();

        manifest.Items = "files";
        manifest.Map = new FeedDownloadMap
        {
            Url = "url",
            FileName = "name",
            SizeBytes = "size",
            Sha1 = "sha1"
        };

        return manifest;
    }

    /// <summary>A minimal valid manifest.</summary>
    /// <returns>The manifest.</returns>
    private static FeedManifest Manifest() => new()
    {
        Key = "test",
        DisplayName = "Test feed",
        Match = new FeedMatch { Hosts = ["a.test"] },
        Request = new FeedRequest { Url = "feed.json" },
        Map = new FeedDownloadMap { Url = "url" }
    };

    /// <summary>Writes a manifest reading <c>feed.json</c> from beside itself.</summary>
    /// <param name="key">The manifest key.</param>
    /// <param name="host">The host it claims.</param>
    /// <returns>YAML text.</returns>
    private static string YamlManifest(string key, string host) =>
        $"""
         key: {key}
         displayName: {key} feed
         match:
           hosts: [{host}]
         request:
           url: feed.json
         format: json
         items: files
         map:
           url: url
           fileName: name

         """;

    private static CatalogListing Listing() => new()
    {
        ListingId = "lst_1",
        Title = "Doom",
        SortTitle = TitleNormalizer.ToSortTitle("Doom"),
        Year = 1993,
        MatchKey = TitleNormalizer.ComputeMatchKey("Doom", 1993),
        PrimarySourceKey = "local",
        ContentHash = "lst_1",
        IsDownloadable = true
    };

    private static byte[] Archive()
    {
        using var buffer = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = archive.CreateEntry("DOOM.EXE").Open())
        {
            var bytes = Encoding.UTF8.GetBytes("MZ fake executable");
            stream.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>A robots policy with a fixed answer, so tests never reach the network.</summary>
    private sealed class FixedRobots(bool allowed) : IRobotsPolicy
    {
        public Task<bool> IsAllowedAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);

        public Task<TimeSpan?> GetCrawlDelayAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(null);
    }
}
