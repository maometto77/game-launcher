using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Sources.SharedCatalog;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers reading a shared catalogue feed.
/// </summary>
/// <remarks>
/// The documents here are written inline rather than captured, because unlike
/// the Archive tests this is a format defined by this project: the interesting
/// thing about each case is the shape of the input next to the assertion, not
/// fidelity to what some live API happens to return today.
/// </remarks>
public sealed class SharedCatalogParserTests
{
    private static readonly Uri FeedUrl = new("https://example.test/feed/catalog.json");

    private static SharedCatalogParseResult Parse(string json) =>
        SharedCatalogParser.Parse(json, FeedUrl, SharedCatalogSource.SourceKey);

    [Fact]
    public void A_well_formed_feed_maps_onto_listings()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "version": 1,
              "name": "The shelf",
              "updated": "2026-08-17T10:00:00Z",
              "entries": [
                {
                  "id": "quake",
                  "title": "Quake",
                  "year": 1996,
                  "description": "Shooting, but vertical.",
                  "developer": "id Software",
                  "publisher": "GT Interactive",
                  "genres": ["Action"],
                  "platforms": ["DOS"],
                  "tags": ["fps"],
                  "updated": "2026-08-01T09:00:00Z",
                  "images": [
                    { "url": "https://cdn.test/quake.jpg", "kind": "cover", "width": 600, "height": 800 }
                  ],
                  "downloads": [
                    {
                      "url": "https://cdn.test/quake.zip",
                      "fileName": "quake.zip",
                      "size": 41943040,
                      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                      "format": "ZIP"
                    }
                  ]
                }
              ]
            }
            """);

        Assert.Equal("The shelf", result.Name);
        Assert.Empty(result.Warnings);

        var listing = Assert.Single(result.Listings);

        Assert.Equal(SharedCatalogSource.SourceKey, listing.SourceKey);
        Assert.Equal("quake", listing.SourceItemId);
        Assert.Equal("Quake", listing.Title);
        Assert.Equal(1996, listing.Year);
        Assert.Equal("id Software", listing.Developer);
        Assert.Equal("GT Interactive", listing.Publisher);
        Assert.Equal(["Action"], listing.Genres);
        Assert.Equal(["DOS"], listing.Platforms);
        Assert.Equal(["fps"], listing.Tags);
        Assert.True(listing.IsDownloadable);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero), listing.SourceUpdatedAt);

        var image = Assert.Single(listing.Images);
        Assert.Equal(ListingImageKind.Cover, image.Kind);
        Assert.Equal(600, image.Width);

        var download = Assert.Single(listing.Downloads);
        Assert.Equal("quake.zip", download.FileName);
        Assert.Equal(41943040, download.SizeBytes);
        Assert.Equal(DownloadKind.Game, download.Kind);
    }

    [Fact]
    public void A_document_that_is_not_a_feed_is_refused()
    {
        // The case this exists for: a URL pointing at some other JSON file. Read
        // permissively it would parse to zero entries and be indistinguishable
        // from a feed the publisher had emptied.
        var exception = Assert.Throws<SharedCatalogFormatException>(() =>
            Parse("""{ "product": "Don", "version": "1.0.0" }"""));

        Assert.Contains("not a catalogue feed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_format_version_is_refused()
    {
        var exception = Assert.Throws<SharedCatalogFormatException>(() =>
            Parse("""{ "feed": "don-catalog", "version": 99, "entries": [] }"""));

        Assert.Contains("version 99", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_is_refused_with_the_address_in_the_message()
    {
        var exception = Assert.Throws<SharedCatalogFormatException>(() => Parse("{ not json"));

        Assert.Contains(FeedUrl.AbsoluteUri, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_missing_an_id_or_a_title_is_skipped_and_reported()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                { "title": "No identifier" },
                { "id": "no-title" },
                { "id": "fine", "title": "Fine" }
              ]
            }
            """);

        // The point of skipping rather than failing: one unusable row must not
        // cost the user every other row in the file.
        var listing = Assert.Single(result.Listings);
        Assert.Equal("fine", listing.SourceItemId);

        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("\"id\"", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("no-title", StringComparison.Ordinal));
    }

    [Fact]
    public void A_repeated_identifier_is_skipped()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                { "id": "doom", "title": "Doom" },
                { "id": "DOOM", "title": "Doom II" }
              ]
            }
            """);

        // Allowing both would make the catalogue depend on import order: each
        // pass, whichever was written last would win.
        var listing = Assert.Single(result.Listings);
        Assert.Equal("Doom", listing.Title);
        Assert.Contains(result.Warnings, warning => warning.Contains("repeats an identifier", StringComparison.Ordinal));
    }

    [Fact]
    public void Relative_addresses_resolve_against_the_feed()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                {
                  "id": "quake", "title": "Quake",
                  "images":    [ { "url": "../art/quake.jpg" } ],
                  "downloads": [ { "url": "files/quake.zip" } ]
                }
              ]
            }
            """);

        var listing = Assert.Single(result.Listings);

        // Why relative addresses are supported: the feed and the files are
        // normally on one host, and this lets that host change without editing
        // a single entry.
        Assert.Equal("https://example.test/feed/files/quake.zip", listing.Downloads[0].Url.AbsoluteUri);
        Assert.Equal("https://example.test/art/quake.jpg", listing.Images[0].Url.AbsoluteUri);
    }

    [Fact]
    public void Addresses_that_are_not_http_are_refused()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                {
                  "id": "quake", "title": "Quake",
                  "downloads": [
                    { "url": "file:///C:/Windows/System32/config/SAM" },
                    { "url": "https://cdn.test/quake.zip" }
                  ]
                }
              ]
            }
            """);

        // A feed is remote content and every address in it is followed by this
        // application. Without this rule a published feed could make the
        // launcher read from the machine running it.
        var listing = Assert.Single(result.Listings);
        var download = Assert.Single(listing.Downloads);

        Assert.Equal("https://cdn.test/quake.zip", download.Url.AbsoluteUri);
        Assert.Contains(result.Warnings, warning => warning.Contains("not an http or https", StringComparison.Ordinal));
    }

    [Fact]
    public void A_prefixed_digest_is_accepted_and_a_malformed_one_is_dropped()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                {
                  "id": "quake", "title": "Quake",
                  "downloads": [
                    {
                      "url": "https://cdn.test/a.zip",
                      "sha256": "sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef"
                    },
                    { "url": "https://cdn.test/b.zip", "sha256": "not-a-digest" }
                  ]
                }
              ]
            }
            """);

        var downloads = Assert.Single(result.Listings).Downloads;

        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            downloads[0].Sha256);

        // Carried forward, a malformed digest would fail verification on a file
        // that downloaded perfectly, and the user would be told their download
        // was corrupt when the feed was simply wrong.
        Assert.Null(downloads[1].Sha256);
        Assert.Null(downloads[1].BestChecksum);
    }

    [Fact]
    public void The_strongest_digest_wins()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                {
                  "id": "quake", "title": "Quake",
                  "downloads": [
                    {
                      "url": "https://cdn.test/a.zip",
                      "md5":    "cccccccccccccccccccccccccccccccc",
                      "sha1":   "dddddddddddddddddddddddddddddddddddddddd",
                      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }
                  ]
                }
              ]
            }
            """);

        var download = Assert.Single(Assert.Single(result.Listings).Downloads);

        // The download path infers the algorithm from the digest's length, so
        // preferring the strongest needs no further plumbing.
        Assert.Equal(download.Sha256, download.BestChecksum);
        Assert.Equal(64, download.BestChecksum!.Length);
    }

    [Fact]
    public void An_entry_with_nothing_to_fetch_is_listed_but_not_installable()
    {
        var result = Parse(
            """
            { "feed": "don-catalog", "entries": [ { "id": "lost", "title": "Something Lost" } ] }
            """);

        // Worth listing — a feed records what exists as much as what can be had —
        // but it must not offer an install button that cannot do anything.
        var listing = Assert.Single(result.Listings);

        Assert.Equal("Something Lost", listing.Title);
        Assert.False(listing.IsDownloadable);
        Assert.Empty(listing.Downloads);
    }

    [Fact]
    public void Document_order_becomes_mirror_order()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                {
                  "id": "quake", "title": "Quake",
                  "downloads": [
                    { "url": "https://near.test/quake.zip" },
                    { "url": "https://far.test/quake.zip" }
                  ]
                }
              ]
            }
            """);

        var downloads = Assert.Single(result.Listings).Downloads;

        // The publisher knows which of their mirrors is nearest; nothing here does.
        Assert.Equal(0, downloads[0].MirrorRank);
        Assert.Equal("near.test", downloads[0].Url.Host);
        Assert.Equal(1, downloads[1].MirrorRank);
    }

    [Fact]
    public void A_missing_file_name_and_format_are_derived_from_the_address()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                { "id": "q", "title": "Q", "downloads": [ { "url": "https://cdn.test/a/Quake%20Shareware.zip" } ] }
              ]
            }
            """);

        var download = Assert.Single(Assert.Single(result.Listings).Downloads);

        Assert.Equal("Quake Shareware.zip", download.FileName);
        Assert.Equal("ZIP", download.Format);
    }

    [Fact]
    public void Quoted_numbers_are_accepted()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                {
                  "id": "q", "title": "Q", "year": "1996",
                  "downloads": [ { "url": "https://cdn.test/a.zip", "size": "1024" } ]
                }
              ]
            }
            """);

        // Hand-written feeds quote their numbers often enough to be worth
        // accepting, and "1996" is unambiguous either way.
        var listing = Assert.Single(result.Listings);

        Assert.Equal(1996, listing.Year);
        Assert.Equal(1024, listing.Downloads[0].SizeBytes);
    }

    [Fact]
    public void The_entry_is_kept_verbatim_including_members_this_build_ignores()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [ { "id": "q", "title": "Q", "somethingNewer": { "a": 1 } } ]
            }
            """);

        // RawPayload is what lets a parser or merge-rule change be re-applied to
        // the whole catalogue offline. Round-tripping through a record would
        // silently drop whatever this build has not learned about yet.
        var listing = Assert.Single(result.Listings);

        Assert.Contains("somethingNewer", listing.RawPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void A_feed_with_no_entries_array_says_so()
    {
        var result = Parse("""{ "feed": "don-catalog", "version": 1 }""");

        Assert.Empty(result.Listings);
        Assert.Contains(result.Warnings, warning => warning.Contains("entries", StringComparison.Ordinal));
    }

    [Fact]
    public void What_the_generator_writes_is_what_the_parser_reads()
    {
        // Captured from an actual run of deploy/feed/build-feed.py over a
        // directory of games. The generator and this parser are the two halves
        // of one format, maintained in different languages in different folders;
        // nothing else would notice them drifting apart.
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Discovery", "Fixtures", "shared-catalog-generated.json"));

        var result = SharedCatalogParser.Parse(
            json, new Uri("https://example.test/feed/catalog.json"), SharedCatalogSource.SourceKey);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Listings.Count);

        // "Quake (1996)" as a folder name, with the year read off it.
        var quake = result.Listings.Single(listing => listing.SourceItemId == "quake-1996");

        Assert.Equal("Quake", quake.Title);
        Assert.Equal(1996, quake.Year);
        Assert.Equal("id Software", quake.Developer);
        Assert.Equal(["Action"], quake.Genres);
        Assert.Equal(ListingImageKind.Cover, quake.Images[0].Kind);

        // The generator percent-encodes each path segment; this is the round
        // trip. Parentheses are escaped and stay escaped: they are legal
        // unencoded in a path, but the generator encodes conservatively and
        // .NET preserves what it was given rather than normalising it. The
        // server decodes either form to the same file.
        var download = Assert.Single(quake.Downloads);

        Assert.Equal(
            "https://example.test/feed/games/Quake%20%281996%29/quake.zip",
            download.Url.AbsoluteUri);
        Assert.Equal(2000000, download.SizeBytes);
        Assert.Equal(64, download.Sha256!.Length);

        // "Doom II [1994]" — the bracket form of the year, and a torrent that
        // must come after the direct address because it needs aria2c.
        var doom = result.Listings.Single(listing => listing.SourceItemId == "doom-ii-1994");

        Assert.Equal("Doom II", doom.Title);
        Assert.Equal(1994, doom.Year);
        Assert.Equal(DownloadKind.Game, doom.Downloads[0].Kind);
        Assert.Equal(DownloadKind.Torrent, doom.Downloads[1].Kind);

        // Ampersands and parentheses in a file name survive both encodings.
        var demo = result.Listings.Single(listing => listing.SourceItemId == "some-demo");

        Assert.Equal("Weird Name & Chars (v1.2).7z", demo.Downloads[0].FileName);
    }

    [Fact]
    public void An_entry_names_its_own_page_or_falls_back_to_the_feed()
    {
        var result = Parse(
            """
            {
              "feed": "don-catalog",
              "entries": [
                { "id": "a", "title": "A", "page": "https://example.test/games/a" },
                { "id": "b", "title": "B" }
              ]
            }
            """);

        Assert.Equal("https://example.test/games/a", result.Listings[0].SourceUrl.AbsoluteUri);
        Assert.Equal(FeedUrl.AbsoluteUri, result.Listings[1].SourceUrl.AbsoluteUri);
    }
}
