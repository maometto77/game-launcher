using System.Net;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Sources.SharedCatalog;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the shared catalogue source: when it is available, how often it goes
/// to the network, and what it does when the feed is not there.
/// </summary>
public sealed class SharedCatalogSourceTests
{
    private const string FeedUrl = "https://example.test/feed/catalog.json";

    private const string Feed =
        """
        {
          "feed": "don-catalog",
          "version": 1,
          "name": "The shelf",
          "entries": [
            {
              "id": "quake", "title": "Quake", "year": 1996,
              "updated": "2026-08-01T09:00:00Z",
              "downloads": [ { "url": "https://cdn.test/quake.zip" } ]
            },
            {
              "id": "doom", "title": "Doom", "year": 1993,
              "updated": "2026-01-01T09:00:00Z",
              "downloads": [ { "url": "https://cdn.test/doom.zip" } ]
            }
          ]
        }
        """;

    [Fact]
    public void The_source_is_unavailable_until_a_feed_is_configured()
    {
        var (unset, _) = Build(url: null);
        Assert.False(unset.IsAvailable);

        // A typo should leave it quietly unavailable exactly as an empty setting
        // does, rather than throwing during an import with other sources to
        // get on with.
        var (nonsense, _) = Build("not a url");
        Assert.False(nonsense.IsAvailable);

        var (wrongScheme, _) = Build("ftp://example.test/catalog.json");
        Assert.False(wrongScheme.IsAvailable);

        var (configured, _) = Build(FeedUrl);
        Assert.True(configured.IsAvailable);
    }

    [Fact]
    public async Task An_unconfigured_source_enumerates_nothing_without_a_request()
    {
        var (source, factory) = Build(url: null);

        Assert.Empty(await Enumerate(source));
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task A_whole_pass_reads_the_feed_once()
    {
        var (source, factory) = Build(FeedUrl, Feed);

        var references = await Enumerate(source);

        foreach (var reference in references)
        {
            Assert.NotNull(await source.FetchAsync(reference));
        }

        // The catalogue is one document. Fetching per entry would be one request
        // per game to re-read a file already in memory.
        Assert.Equal(2, references.Count);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task An_entry_maps_through_to_a_listing()
    {
        var (source, _) = Build(FeedUrl, Feed);

        var listing = await source.FetchAsync(Reference("quake"));

        Assert.NotNull(listing);
        Assert.Equal("Quake", listing.Title);
        Assert.Equal(1996, listing.Year);
        Assert.Equal(SharedCatalogSource.SourceKey, listing.SourceKey);
        Assert.True(listing.IsDownloadable);
    }

    [Fact]
    public async Task An_entry_the_publisher_has_removed_returns_null()
    {
        var (source, _) = Build(FeedUrl, Feed);

        // Null rather than throwing: a feed republished without an entry is the
        // publisher removing a game, which is a reason to skip it permanently
        // rather than to retry it later.
        Assert.Null(await source.FetchAsync(Reference("never-existed")));
    }

    [Fact]
    public async Task An_unreachable_feed_costs_this_source_and_not_the_import()
    {
        var factory = new StubHttpClientFactory().Status("catalog.json", HttpStatusCode.NotFound);
        var source = Source(factory, FeedUrl);

        Assert.Empty(await Enumerate(source));
        Assert.Null(await source.FetchAsync(Reference("quake")));
    }

    [Fact]
    public async Task A_document_that_is_not_a_feed_is_reported_and_yields_nothing()
    {
        // The likely mistake is pointing this at release.json, which is a real
        // file served from the same host.
        var (source, _) = Build(FeedUrl, """{ "product": "Don", "version": "1.0.0" }""");

        Assert.Empty(await Enumerate(source));
    }

    [Fact]
    public async Task A_feed_the_host_disallows_is_not_fetched()
    {
        var factory = new StubHttpClientFactory().Json("catalog.json", Feed);

        var source = new SharedCatalogSource(
            factory,
            new FixedSettings(FeedUrl),
            new FixedRobots(allowed: false),
            NullLogger<SharedCatalogSource>.Instance);

        Assert.Empty(await Enumerate(source));

        // Trivially under the publisher's own control, so this fires when the
        // setting points at somebody else's document — which is when it should.
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task ChangedSince_skips_entries_the_publisher_has_not_touched()
    {
        var (source, _) = Build(FeedUrl, Feed);

        var references = await Enumerate(source, new SourceEnumerationOptions
        {
            ChangedSince = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
        });

        var reference = Assert.Single(references);
        Assert.Equal("quake", reference.SourceItemId);
    }

    [Fact]
    public async Task MaxItems_stops_the_pass()
    {
        var (source, _) = Build(FeedUrl, Feed);

        var references = await Enumerate(source, new SourceEnumerationOptions { MaxItems = 1 });

        Assert.Single(references);
    }

    [Fact]
    public async Task The_feed_names_itself_for_display()
    {
        var (source, _) = Build(FeedUrl, Feed);

        Assert.Equal("Shared catalogue", source.DisplayName);

        await Enumerate(source);

        // Once read, it introduces itself by the name its publisher chose.
        Assert.Equal("The shelf", source.DisplayName);
    }

    [Fact]
    public void It_outranks_every_source_that_reads_somebody_elses_site()
    {
        var (source, _) = Build(FeedUrl);

        // It is the only source describing files its publisher actually holds.
        Assert.True(source.Rank < 0);
    }

    private static SourceListingRef Reference(string id) =>
        new(SharedCatalogSource.SourceKey, id, id, null, null);

    private static async Task<IReadOnlyList<SourceListingRef>> Enumerate(
        SharedCatalogSource source,
        SourceEnumerationOptions? options = null)
    {
        var references = new List<SourceListingRef>();

        await foreach (var reference in source.EnumerateAsync(options ?? new SourceEnumerationOptions()))
        {
            references.Add(reference);
        }

        return references;
    }

    private static (SharedCatalogSource Source, StubHttpClientFactory Factory) Build(
        string? url,
        string? feed = null)
    {
        var factory = new StubHttpClientFactory();

        if (feed is not null)
        {
            factory.Json("catalog.json", feed);
        }

        return (Source(factory, url), factory);
    }

    private static SharedCatalogSource Source(StubHttpClientFactory factory, string? url) =>
        new(factory, new FixedSettings(url), new FixedRobots(allowed: true),
            NullLogger<SharedCatalogSource>.Instance);

    /// <summary>Settings with only the feed address set, and no file behind them.</summary>
    private sealed class FixedSettings(string? url) : ISettingsService
    {
        public AppSettings Current { get; private set; } = new() { SharedCatalogUrl = url };

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }

    /// <summary>A robots policy that answers the same way every time.</summary>
    private sealed class FixedRobots(bool allowed) : IRobotsPolicy
    {
        public Task<bool> IsAllowedAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);

        public Task<TimeSpan?> GetCrawlDelayAsync(Uri address, CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeSpan?>(null);
    }
}
