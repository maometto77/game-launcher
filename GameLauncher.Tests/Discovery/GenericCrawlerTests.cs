using System.Net;
using GameLauncher.Desktop.Services.Discovery.Crawling;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the generic crawler against a site served on the loopback interface.
/// </summary>
/// <remarks>
/// Every page these tests read is one they wrote. That is the point: a real site
/// cannot be asked for a malformed page, a paginator that loops, or a body that
/// claims one size and sends another, and those are exactly the cases a crawler
/// has to survive.
/// </remarks>
public sealed class GenericCrawlerTests
{
    /// <summary>A listing page with two games and an optional next link.</summary>
    /// <param name="entries">The entry markup.</param>
    /// <param name="next">The next page's address, or null.</param>
    /// <returns>HTML.</returns>
    private static string Listing(string entries, string? next = null) =>
        $"""
         <!doctype html><html><head><title>Games</title></head><body>
         <main>{entries}</main>
         {(next is null ? "" : $"<nav class='pagination'><a class='next' href='{next}'>Next</a></nav>")}
         </body></html>
         """;

    /// <summary>One listing entry in the ordinary shape.</summary>
    /// <param name="href">Where it links.</param>
    /// <param name="title">What it is called.</param>
    /// <returns>HTML.</returns>
    private static string Entry(string href, string title) =>
        $"<article class='game'><h2><a href='{href}'>{title}</a></h2></article>";

    /// <summary>Builds a crawler over a real page fetcher.</summary>
    /// <param name="allowPrivate">Whether loopback addresses are permitted.</param>
    /// <returns>The crawler and the policy to run it under.</returns>
    /// <remarks>
    /// The address policy is the one thing relaxed: the whole suite runs against
    /// 127.0.0.1, which the guard refuses by default and rightly so. Everything
    /// else — robots, retries, limits — is the production path.
    /// </remarks>
    private static (GenericWebCrawler Crawler, UrlPolicy Policy) Build(TestAppHost host)
    {
        // The real client factory and the real robots policy, from the same
        // container the application builds. Only the address policy is relaxed,
        // because the whole suite runs against 127.0.0.1 and the guard refuses
        // that by default — rightly, which is why relaxing it is explicit here
        // rather than a hole in the guard.
        var factory = host.Resolve<System.Net.Http.IHttpClientFactory>();

        var fetcher = new PageFetcher(
            factory,
            host.Resolve<IRobotsPolicy>(),
            NullLogger.Instance);

        return (new GenericWebCrawler(fetcher, NullLogger.Instance),
            UrlPolicy.Default with { AllowPrivateAddresses = true });
    }

    /// <summary>Drains a crawl into a list.</summary>
    /// <param name="site">The site to crawl.</param>
    /// <param name="start">Where to start.</param>
    /// <param name="selectors">Selector overrides.</param>
    /// <param name="limits">The bounds to run inside.</param>
    /// <param name="diagnostics">Where to record what happened.</param>
    /// <param name="query">A search term, or null.</param>
    /// <param name="cancellationToken">Cancels the crawl.</param>
    /// <returns>The items found.</returns>
    private static async Task<List<CrawledItem>> CrawlAsync(
        LoopbackSiteServer site,
        TestAppHost host,
        string start = "/games/",
        CrawlSelectors? selectors = null,
        CrawlLimits? limits = null,
        CrawlDiagnostics? diagnostics = null,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var (crawler, policy) = Build(host);

        var request = new CrawlRequest(
            site.Url(start),
            selectors ?? new CrawlSelectors(),
            limits ?? new CrawlLimits { Delay = TimeSpan.Zero },
            policy)
        { Query = query };

        var found = new List<CrawledItem>();

        await foreach (var item in crawler
                           .CrawlAsync(request, diagnostics ?? new CrawlDiagnostics(), cancellationToken)
                           .ConfigureAwait(false))
        {
            found.Add(item);
        }

        return found;
    }

    [Fact]
    public async Task A_listing_page_is_read_without_any_selectors()
    {
        // The whole promise of the thing: one address, no configuration.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(
            Entry("/games/doom", "Doom") + Entry("/games/quake", "Quake")));

        var items = await CrawlAsync(site, host);

        Assert.Equal(["Doom", "Quake"], items.Select(item => item.Title));
        Assert.EndsWith("/games/doom", items[0].DetailAddress.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pagination_is_followed_to_the_end()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(Entry("/g/1", "One") + Entry("/g/2", "Two"), "/games/page/2/"));
        site.AddPage("/games/page/2/", Listing(Entry("/g/3", "Three") + Entry("/g/4", "Four")));

        var items = await CrawlAsync(site, host);

        Assert.Equal(["One", "Two", "Three", "Four"], items.Select(item => item.Title));
    }

    [Fact]
    public async Task A_paginator_that_loops_back_stops_the_crawl()
    {
        // The ordinary shape of endless pagination, and the reason the engine
        // remembers where it has been rather than trusting MaxPages alone.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(Entry("/g/1", "One") + Entry("/g/2", "Two"), "/games/page/2/"));
        site.AddPage("/games/page/2/", Listing(Entry("/g/3", "Three") + Entry("/g/4", "Four"), "/games/"));

        var items = await CrawlAsync(site, host, limits: new CrawlLimits
        {
            Delay = TimeSpan.Zero,
            MaxPages = 500
        });

        Assert.Equal(4, items.Count);

        // Two pages, not five hundred.
        Assert.Equal(2, site.Requests.Count(path => path.StartsWith("/games", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_paginator_that_never_ends_is_stopped_by_the_page_cap()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        // Every page offers a fresh next page, for ever.
        for (var page = 1; page <= 20; page++)
        {
            site.AddPage(
                page == 1 ? "/games/" : $"/games/page/{page}/",
                Listing(
                    Entry($"/g/{page}a", $"Game {page}A") + Entry($"/g/{page}b", $"Game {page}B"),
                    $"/games/page/{page + 1}/"));
        }

        var items = await CrawlAsync(site, host, limits: new CrawlLimits
        {
            Delay = TimeSpan.Zero,
            MaxPages = 3
        });

        Assert.Equal(6, items.Count);
    }

    [Fact]
    public async Task The_item_cap_stops_a_crawl_mid_page()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(
            Entry("/g/1", "One") + Entry("/g/2", "Two") + Entry("/g/3", "Three")));

        var items = await CrawlAsync(site, host, limits: new CrawlLimits
        {
            Delay = TimeSpan.Zero,
            MaxItems = 2
        });

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Selector_overrides_replace_the_guesses()
    {
        // A page laid out so the inference would find the wrong thing: the
        // sidebar posts look exactly like the listing entries.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", """
            <!doctype html><html><body>
            <aside><article class='post'><h2><a href='/blog/news'>Site news</a></h2></article></aside>
            <main>
              <div class='release'><span class='name'><a href='/g/doom'>Doom</a></span></div>
              <div class='release'><span class='name'><a href='/g/quake'>Quake</a></span></div>
            </main>
            </body></html>
            """);

        var items = await CrawlAsync(site, host, selectors: new CrawlSelectors
        {
            Item = ".release",
            DetailLink = ".name a"
        });

        Assert.Equal(["Doom", "Quake"], items.Select(item => item.Title));
    }

    [Fact]
    public async Task The_same_game_linked_twice_is_reported_once()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(
            Entry("/g/doom", "Doom") + Entry("/g/doom", "Doom again") + Entry("/g/quake", "Quake")));

        var diagnostics = new CrawlDiagnostics();
        var items = await CrawlAsync(site, host, diagnostics: diagnostics);

        Assert.Equal(["Doom", "Quake"], items.Select(item => item.Title));
        Assert.True(diagnostics.DuplicatesSkipped > 0);
    }

    [Fact]
    public async Task A_game_listed_on_two_pages_is_reported_once()
    {
        // What happens whenever a site paginates a feed still being written to.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(Entry("/g/1", "One") + Entry("/g/2", "Two"), "/games/page/2/"));
        site.AddPage("/games/page/2/", Listing(Entry("/g/2", "Two") + Entry("/g/3", "Three")));

        var items = await CrawlAsync(site, host);

        Assert.Equal(["One", "Two", "Three"], items.Select(item => item.Title));
    }

    [Fact]
    public async Task Addresses_are_canonicalised_before_they_are_compared()
    {
        // The same page reached by two spellings is one page.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(
            Entry("/g/doom", "Doom") + Entry("/g/doom#reviews", "Doom reviews")));

        var items = await CrawlAsync(site, host);

        Assert.Single(items);
    }

    [Fact]
    public async Task A_crawl_is_confined_to_the_site_it_started_on()
    {
        // One outbound link would otherwise turn one configured source into a
        // walk of the open web.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(
            Entry("/g/doom", "Doom") +
            Entry("https://elsewhere.test/g/other", "Somewhere else") +
            Entry("/g/quake", "Quake")));

        var diagnostics = new CrawlDiagnostics();
        var items = await CrawlAsync(site, host, diagnostics: diagnostics);

        Assert.Equal(["Doom", "Quake"], items.Select(item => item.Title));
        Assert.True(diagnostics.LinksRejected > 0);
    }

    [Fact]
    public async Task Robots_denial_stops_the_crawl_before_any_page_is_read()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.Robots = "User-agent: *\nDisallow: /games/";
        site.AddPage("/games/", Listing(Entry("/g/doom", "Doom") + Entry("/g/quake", "Quake")));

        var diagnostics = new CrawlDiagnostics();
        var items = await CrawlAsync(site, host, diagnostics: diagnostics);

        Assert.Empty(items);
        Assert.Equal(1, diagnostics.RobotsDenied);

        // The listing page itself was never requested.
        Assert.DoesNotContain("/games/", site.Requests);
    }

    [Fact]
    public async Task A_transient_failure_is_retried()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.FailFirstRequests = 2;
        site.AddPage("/games/", Listing(Entry("/g/doom", "Doom") + Entry("/g/quake", "Quake")));

        var diagnostics = new CrawlDiagnostics();

        var items = await CrawlAsync(site, host, limits: new CrawlLimits
        {
            Delay = TimeSpan.Zero,
            Retries = 4
        }, diagnostics: diagnostics);

        Assert.Equal(2, items.Count);
        Assert.True(diagnostics.Retries >= 2);
    }

    [Fact]
    public async Task A_page_that_keeps_failing_ends_the_crawl_rather_than_hanging()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddStatus("/games/", HttpStatusCode.InternalServerError);

        var diagnostics = new CrawlDiagnostics();

        var items = await CrawlAsync(site, host, limits: new CrawlLimits
        {
            Delay = TimeSpan.Zero,
            Retries = 2
        }, diagnostics: diagnostics);

        Assert.Empty(items);
        Assert.Equal(1, diagnostics.PagesFailed);
    }

    [Fact]
    public async Task A_body_larger_than_the_limit_is_refused()
    {
        // A page is a document. Reading without a bound turns one misconfigured
        // route into an out-of-memory failure.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(
            Entry("/g/doom", "Doom") + Entry("/g/quake", "Quake") + new string('x', 200_000)));

        var diagnostics = new CrawlDiagnostics();

        var items = await CrawlAsync(site, host, limits: new CrawlLimits
        {
            Delay = TimeSpan.Zero,
            MaxResponseBytes = 8192,
            Retries = 1
        }, diagnostics: diagnostics);

        Assert.Empty(items);
        Assert.Equal(1, diagnostics.PagesFailed);
    }

    [Fact]
    public async Task A_binary_response_is_not_parsed_as_a_page()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddContent("/games/", "MZ\0\0binary", "application/octet-stream");

        var diagnostics = new CrawlDiagnostics();

        var items = await CrawlAsync(site, host, limits: new CrawlLimits
        {
            Delay = TimeSpan.Zero,
            Retries = 1
        }, diagnostics: diagnostics);

        Assert.Empty(items);
        Assert.Equal(1, diagnostics.PagesFailed);
    }

    [Fact]
    public async Task Malformed_markup_is_read_as_far_as_it_goes()
    {
        // Browsers recover from this and so must a crawler: unclosed tags are
        // the normal state of the web, not an error.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", """
            <!doctype html><html><body><main>
            <article class='game'><h2><a href='/g/doom'>Doom
            <article class='game'><h2><a href='/g/quake'>Quake</a>
            </main>
            """);

        var items = await CrawlAsync(site, host);

        Assert.NotEmpty(items);
        Assert.Contains(items, item => item.Title!.Contains("Doom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_with_no_title_is_skipped_rather_than_imported_blank()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(
            "<article class='game'><h2><a href='/g/blank'></a></h2></article>" +
            Entry("/g/doom", "Doom") +
            Entry("/g/quake", "Quake")));

        var diagnostics = new CrawlDiagnostics();
        var items = await CrawlAsync(site, host, diagnostics: diagnostics);

        Assert.Equal(["Doom", "Quake"], items.Select(item => item.Title));
    }

    [Fact]
    public async Task A_search_term_narrows_what_the_crawl_reports()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(
            Entry("/g/doom", "Doom") + Entry("/g/quake", "Quake") + Entry("/g/doom2", "Doom II")));

        var items = await CrawlAsync(site, host, query: "doom");

        Assert.Equal(["Doom", "Doom II"], items.Select(item => item.Title));
    }

    [Fact]
    public async Task A_crawl_can_be_cancelled_mid_flight()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        for (var page = 1; page <= 10; page++)
        {
            site.AddPage(
                page == 1 ? "/games/" : $"/games/page/{page}/",
                Listing(Entry($"/g/{page}", $"Game {page}"), $"/games/page/{page + 1}/"));
        }

        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var (crawler, policy) = Build(host);

            var request = new CrawlRequest(
                site.Url("/games/"),
                new CrawlSelectors(),
                new CrawlLimits { Delay = TimeSpan.Zero },
                policy);

            await foreach (var _ in crawler.CrawlAsync(request, new CrawlDiagnostics(), cancellation.Token))
            {
                // Cancelled after the first item, which is what a user closing
                // the page mid-import looks like.
                await cancellation.CancelAsync();
            }
        });
    }

    [Fact]
    public async Task A_crawl_resumes_from_the_page_a_cursor_names()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(Entry("/g/1", "One") + Entry("/g/2", "Two"), "/games/page/2/"));
        site.AddPage("/games/page/2/", Listing(Entry("/g/3", "Three") + Entry("/g/4", "Four")));

        var (crawler, policy) = Build(host);

        var request = new CrawlRequest(
            site.Url("/games/"),
            new CrawlSelectors(),
            new CrawlLimits { Delay = TimeSpan.Zero },
            policy)
        { Cursor = site.Url("/games/page/2/").AbsoluteUri };

        var items = new List<CrawledItem>();

        await foreach (var item in crawler.CrawlAsync(request, new CrawlDiagnostics()))
        {
            items.Add(item);
        }

        // The first page is skipped entirely: the cursor said it was done.
        Assert.Equal(["Three", "Four"], items.Select(item => item.Title));
    }

    [Fact]
    public async Task The_cursor_names_the_page_an_item_was_found_on()
    {
        // Resuming replays the page a kill interrupted rather than the one
        // after it, which the pipeline's content hashes make nearly free —
        // whereas resuming one page too late would silently skip it.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(Entry("/g/1", "One"), "/games/page/2/"));
        site.AddPage("/games/page/2/", Listing(Entry("/g/2", "Two")));

        var items = await CrawlAsync(site, host);

        Assert.EndsWith("/games/", items[0].Cursor, StringComparison.Ordinal);
        Assert.EndsWith("/games/page/2/", items[1].Cursor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_identity_is_stable_across_crawls()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", Listing(Entry("/g/doom", "Doom") + Entry("/g/quake", "Quake")));

        var first = await CrawlAsync(site, host);
        var second = await CrawlAsync(site, host);

        Assert.Equal(
            first.Select(item => item.SourceId),
            second.Select(item => item.SourceId));

        // The scheme is left out, so a site moving to HTTPS does not duplicate
        // its whole catalogue.
        Assert.DoesNotContain("http", first[0].SourceId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plain_index_page_is_read_by_its_links()
    {
        // No repeated container to recognise: a hand-written list of releases,
        // which is what plenty of small self-hosted indexes actually are.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", """
            <!doctype html><html><body><main>
            <p>Our releases:</p>
            <a href="/games/doom">Doom</a>
            <a href="/games/quake">Quake</a>
            <a href="/about">About us</a>
            </main></body></html>
            """);

        var items = await CrawlAsync(site, host);

        // 'About' is filtered as site furniture; the two games are not.
        Assert.Equal(["Doom", "Quake"], items.Select(item => item.Title));
    }

    [Fact]
    public async Task Diagnostics_report_a_crawl_that_read_pages_and_found_nothing()
    {
        // The shape of a site redesigned under a working set of selectors, and
        // the difference between that and an empty site.
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        site.AddPage("/games/", "<!doctype html><html><body><p>Nothing here.</p></body></html>");

        var diagnostics = new CrawlDiagnostics();

        await CrawlAsync(site, host, diagnostics: diagnostics);

        Assert.Equal(1, diagnostics.PagesFetched);
        Assert.Equal(0, diagnostics.ItemsFound);
        Assert.False(diagnostics.LooksHealthy);
        Assert.Contains("page(s)", diagnostics.Summarize(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_of_unrecognisable_pages_abandons_the_crawl()
    {
        using var host = new TestAppHost();
        await using var site = await LoopbackSiteServer.StartAsync();

        for (var page = 1; page <= 10; page++)
        {
            site.AddPage(
                page == 1 ? "/games/" : $"/games/page/{page}/",
                $"<!doctype html><html><body><p>Page {page}</p>" +
                $"<nav class='pagination'><a class='next' href='/games/page/{page + 1}/'>Next</a></nav>" +
                "</body></html>");
        }

        var diagnostics = new CrawlDiagnostics();

        await CrawlAsync(site, host, limits: new CrawlLimits
        {
            Delay = TimeSpan.Zero,
            MaxPages = 100,
            MaxConsecutiveFailures = 3
        }, diagnostics: diagnostics);

        // Three barren pages, not a hundred.
        Assert.Equal(3, diagnostics.PagesFetched);
    }
}
