using System.Net;
using GameLauncher.Desktop.Services.Discovery.Crawling;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Covers the gate every crawled and resolved address passes through.
/// </summary>
/// <remarks>
/// The single most security-relevant component of the crawler, because every
/// address it judges came out of a web page or an external program — which is to
/// say, was chosen by somebody else.
/// </remarks>
public sealed class UrlGuardTests
{
    [Theory]
    [InlineData("https://example.test/games/", "https://example.test/games/")]
    [InlineData("https://example.test/games/#reviews", "https://example.test/games/")]
    [InlineData("http://example.test:80/a", "http://example.test/a")]
    [InlineData("https://example.test:443/a", "https://example.test/a")]
    public void Addresses_are_canonicalised_so_the_same_page_dedupes(string input, string expected) =>
        Assert.Equal(expected, UrlGuard.Canonicalize(input)?.AbsoluteUri);

    [Theory]
    [InlineData("/games/doom", "https://example.test/games/doom")]
    [InlineData("doom", "https://example.test/games/doom")]
    [InlineData("../about", "https://example.test/about")]
    [InlineData("//cdn.example.test/x.png", "https://cdn.example.test/x.png")]
    public void Relative_addresses_resolve_against_the_page(string href, string expected)
    {
        var page = new Uri("https://example.test/games/");

        Assert.Equal(expected, UrlGuard.Canonicalize(href, page)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.test/x.zip")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    public void Unsupported_schemes_are_refused(string url)
    {
        var verdict = UrlGuard.Inspect(url, UrlPolicy.Default);

        Assert.False(verdict.IsAllowed);

        // Malformed and unsupported are both refusals; which one matters only
        // for the message, and either is a refusal the caller must honour.
        Assert.Contains(
            verdict.Rejection,
            new[] { UrlRejection.UnsupportedScheme, UrlRejection.Malformed });
    }

    [Theory]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://localhost/x")]
    [InlineData("http://10.0.0.5/x")]
    [InlineData("http://192.168.1.10/x")]
    [InlineData("http://172.16.4.4/x")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://nas.local/x")]
    [InlineData("http://0.0.0.0/x")]
    public void Private_and_local_addresses_are_refused(string url)
    {
        // 169.254.169.254 is the cloud metadata address, which is the single
        // most valuable SSRF target there is and looks like an ordinary link.
        var verdict = UrlGuard.Inspect(url, UrlPolicy.Default);

        Assert.False(verdict.IsAllowed);
        Assert.Equal(UrlRejection.PrivateAddress, verdict.Rejection);
    }

    [Fact]
    public void A_private_address_is_allowed_when_the_manifest_asked_for_it()
    {
        // A repository genuinely hosted on the local network is a real case, and
        // it is opt-in rather than the default for the obvious reason.
        var policy = UrlPolicy.Default with { AllowPrivateAddresses = true };

        Assert.True(UrlGuard.Inspect("http://127.0.0.1:8080/games/", policy).IsAllowed);
    }

    [Theory]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.64.0.1", true)]
    [InlineData("224.0.0.1", true)]
    public void Address_ranges_are_judged_correctly(string address, bool isPrivate) =>
        Assert.Equal(isPrivate, UrlGuard.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void An_ipv4_address_wrapped_in_ipv6_is_still_judged_as_ipv4()
    {
        // ::ffff:127.0.0.1 reaches loopback while looking like a v6 address, and
        // judging it by its family alone would let it through.
        Assert.True(UrlGuard.IsPrivate(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.True(UrlGuard.IsPrivate(IPAddress.Parse("::ffff:10.0.0.1")));
    }

    [Fact]
    public void A_host_outside_the_allow_list_is_refused()
    {
        var policy = UrlPolicy.Default.ConfinedTo("example.test");

        Assert.True(UrlGuard.Inspect("https://example.test/a", policy).IsAllowed);

        // A suffix match, so subdomains of an allowed host are allowed too.
        Assert.True(UrlGuard.Inspect("https://cdn.example.test/a", policy).IsAllowed);

        var refused = UrlGuard.Inspect("https://elsewhere.test/a", policy);

        Assert.False(refused.IsAllowed);
        Assert.Equal(UrlRejection.HostNotAllowed, refused.Rejection);
    }

    [Fact]
    public void A_blocked_host_is_refused_even_when_it_is_also_allowed()
    {
        // Blocking wins, so a broad allow list can still have holes cut in it.
        var policy = UrlPolicy.Default with
        {
            AllowedHosts = ["example.test"],
            BlockedHosts = ["ads.example.test"]
        };

        Assert.Equal(
            UrlRejection.HostBlocked,
            UrlGuard.Inspect("https://ads.example.test/a", policy).Rejection);
    }

    [Fact]
    public void A_magnet_is_refused_unless_the_manifest_allowed_it()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

        Assert.False(UrlGuard.Inspect(magnet, UrlPolicy.Default).IsAllowed);

        // Permitted only where a manifest asked, because a magnet needs an
        // external engine that may not be installed.
        Assert.True(UrlGuard.Inspect(magnet, UrlPolicy.WithMagnet).IsAllowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("http://")]
    [InlineData("///")]
    public void Malformed_addresses_are_refused_rather_than_throwing(string? url)
    {
        var verdict = UrlGuard.Inspect(url, UrlPolicy.Default);

        Assert.False(verdict.IsAllowed);
        Assert.Null(verdict.Address);
    }

    [Fact]
    public void A_relative_address_alone_is_not_fetchable()
    {
        Assert.Null(UrlGuard.Canonicalize("/games/doom"));
        Assert.False(UrlGuard.Inspect("/games/doom", UrlPolicy.Default).IsAllowed);
    }
}
