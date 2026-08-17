using GameLauncher.Desktop.Services.Discovery.Http;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers robots.txt parsing and matching. These rules decide what the crawler
/// is allowed to touch, so the tests that assert something is <em>refused</em>
/// matter most.
/// </summary>
public sealed class RobotsPolicyTests
{
    /// <summary>
    /// The real rules published by myabandonware.com, captured while building
    /// the source that reads it.
    /// </summary>
    private const string MyAbandonwareRobots = """
        Sitemap: https://www.myabandonware.com/sitemap.xml.gz

        User-agent: *
        Disallow: /download/*
        Disallow: /manual/*
        Disallow: /game/rate/*
        Disallow: /game/comment/*
        Disallow: /game/playcomment/*
        Disallow: /game/vote/*
        Disallow: /game/playstat/*
        Disallow: /favorites/*
        Disallow: /search/*
        """;

    [Fact]
    public void The_real_rules_permit_metadata_pages()
    {
        var rules = RobotsPolicy.Parse(MyAbandonwareRobots);

        Assert.True(rules.IsAllowed("/game/doom"));
        Assert.True(rules.IsAllowed("/browse/name/D"));
        Assert.True(rules.IsAllowed("/"));
    }

    [Fact]
    public void The_real_rules_refuse_download_and_search_paths()
    {
        // This is the finding that shaped the MyAbandonware source: its download
        // paths are off limits, so the source contributes metadata only.
        var rules = RobotsPolicy.Parse(MyAbandonwareRobots);

        Assert.False(rules.IsAllowed("/download/12345/doom"));
        Assert.False(rules.IsAllowed("/search/doom"));
        Assert.False(rules.IsAllowed("/manual/doom"));
        Assert.False(rules.IsAllowed("/game/rate/doom"));
        Assert.False(rules.IsAllowed("/game/comment/doom"));
        Assert.False(rules.IsAllowed("/favorites/mine"));
    }

    [Fact]
    public void An_absent_file_permits_everything()
    {
        // The convention: no published rules means no restrictions. Refusing
        // instead would make a crawler unusable rather than polite.
        Assert.True(RobotsPolicy.Parse(null).IsAllowed("/anything"));
        Assert.True(RobotsPolicy.Parse(string.Empty).IsAllowed("/anything"));
    }

    [Fact]
    public void A_blanket_disallow_refuses_everything()
    {
        var rules = RobotsPolicy.Parse("User-agent: *\nDisallow: /");

        Assert.False(rules.IsAllowed("/"));
        Assert.False(rules.IsAllowed("/game/doom"));
    }

    [Fact]
    public void An_empty_disallow_is_not_a_rule()
    {
        // "Disallow:" with no value is the documented way to say "nothing is
        // disallowed". Reading it as a prefix match on "" would block the site.
        var rules = RobotsPolicy.Parse("User-agent: *\nDisallow:");

        Assert.True(rules.IsAllowed("/game/doom"));
    }

    [Fact]
    public void A_more_specific_allow_beats_a_disallow()
    {
        var rules = RobotsPolicy.Parse("User-agent: *\nDisallow: /game/\nAllow: /game/public/");

        Assert.False(rules.IsAllowed("/game/private/x"));
        Assert.True(rules.IsAllowed("/game/public/x"));
    }

    [Fact]
    public void Rules_for_other_agents_are_ignored()
    {
        // Only the wildcard group is read. Claiming a named identity to obtain
        // looser rules would defeat the point of asking.
        var rules = RobotsPolicy.Parse("""
            User-agent: Googlebot
            Disallow: /

            User-agent: *
            Disallow: /private/
            """);

        Assert.True(rules.IsAllowed("/game/doom"));
        Assert.False(rules.IsAllowed("/private/x"));
    }

    [Fact]
    public void A_named_group_after_the_wildcard_does_not_loosen_it()
    {
        var rules = RobotsPolicy.Parse("""
            User-agent: *
            Disallow: /private/

            User-agent: Googlebot
            Allow: /private/
            """);

        Assert.False(rules.IsAllowed("/private/x"));
    }

    [Fact]
    public void Comments_and_blank_lines_are_skipped()
    {
        var rules = RobotsPolicy.Parse("""
            # a comment
            User-agent: *   # trailing comment

            Disallow: /private/   # another
            """);

        Assert.False(rules.IsAllowed("/private/x"));
        Assert.True(rules.IsAllowed("/public/x"));
    }

    [Fact]
    public void An_end_anchor_matches_only_the_whole_path()
    {
        var rules = RobotsPolicy.Parse("User-agent: *\nDisallow: /*.pdf$");

        Assert.False(rules.IsAllowed("/manuals/doom.pdf"));
        Assert.True(rules.IsAllowed("/manuals/doom.pdf.html"));
    }

    [Fact]
    public void An_interior_wildcard_matches_across_segments()
    {
        var rules = RobotsPolicy.Parse("User-agent: *\nDisallow: /game/*/private");

        Assert.False(rules.IsAllowed("/game/doom/private"));
        Assert.True(rules.IsAllowed("/game/doom/public"));
    }

    [Fact]
    public void A_crawl_delay_is_read_when_stated()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(2.5),
            RobotsPolicy.Parse("User-agent: *\nCrawl-delay: 2.5").CrawlDelay);

        Assert.Null(RobotsPolicy.Parse("User-agent: *\nDisallow: /x").CrawlDelay);

        // An absurd value is ignored rather than trusted: a crawl delay of a day
        // would silently stop imports with nothing to explain it.
        Assert.Null(RobotsPolicy.Parse("User-agent: *\nCrawl-delay: 100000").CrawlDelay);
    }

    [Fact]
    public void Consecutive_agent_lines_share_one_group()
    {
        var rules = RobotsPolicy.Parse("""
            User-agent: SomeBot
            User-agent: *
            Disallow: /private/
            """);

        Assert.False(rules.IsAllowed("/private/x"));
    }
}
