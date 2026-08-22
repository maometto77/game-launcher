using AngleSharp.Dom;

namespace GameLauncher.Desktop.Services.Discovery.Crawling;

/// <summary>
/// One entry found on a listing page.
/// </summary>
/// <param name="Title">The title as the listing showed it, when it showed one.</param>
/// <param name="DetailAddress">The page describing the game.</param>
public sealed record ListingEntry(string? Title, Uri DetailAddress);

/// <summary>
/// What one listing page yielded.
/// </summary>
/// <param name="Entries">The entries found, in document order.</param>
/// <param name="NextPage">The following page, when there is one.</param>
/// <param name="ItemSelector">The selector the entries were found with, for diagnostics.</param>
public sealed record ListingPage(
    IReadOnlyList<ListingEntry> Entries,
    Uri? NextPage,
    string? ItemSelector);

/// <summary>
/// Finds the games, and the next page, on a listing page.
/// </summary>
/// <remarks>
/// <para>
/// The inference here is deliberately unambitious. It looks for the shape almost
/// every catalogue page on the web has — a repeated block, each containing a
/// link to a page about one thing — and it stops as soon as it finds something
/// that fits. It is not trying to understand the page; it is trying to find the
/// one pattern that is worth guessing at, and to be overridden cheaply when the
/// guess is wrong.
/// </para>
/// <para>
/// That is why <see cref="CrawlSelectors.Item"/> exists. A site nobody
/// anticipated needs one line of CSS, not a cleverer heuristic, and a heuristic
/// clever enough to handle every site would be one nobody could predict.
/// </para>
/// </remarks>
public static class ListingPageParser
{
    /// <summary>Containers a listing entry is usually in, most specific first.</summary>
    private static readonly string[] ItemCandidates =
    [
        "article.game", "article.post", "li.game", "div.game", ".game-item", ".game-card",
        "article", ".post", ".entry", ".card", ".item", ".listing-item", ".search-result",
        "li.post", "tr.game", "tbody tr", "ul.games > li", ".games > li", ".grid > li",
    ];

    /// <summary>Fewest entries a candidate selector must find to be believed.</summary>
    /// <remarks>
    /// Two, because one match is as likely to be the page's own header as a
    /// game, and a listing page with a single game on it is rare enough that
    /// falling through to the link scan costs nothing.
    /// </remarks>
    private const int MinimumEntries = 2;

    /// <summary>Selectors that name the next page, most reliable first.</summary>
    private static readonly string[] NextCandidates =
    [
        "link[rel='next']", "a[rel='next']", "a.next", ".next > a", ".pagination a.next",
        ".pagination .next a", ".nav-links a.next", "a.nextpostslink", "a[aria-label*='Next' i]",
    ];

    /// <summary>Link text that means "the next page".</summary>
    private static readonly string[] NextWords = ["next", "next page", "older", "older posts", "›", "»", "→", ">"];

    /// <summary>
    /// Reads a listing page.
    /// </summary>
    /// <param name="page">The page to read.</param>
    /// <param name="selectors">Selector overrides, possibly empty.</param>
    /// <param name="policy">What addresses are acceptable.</param>
    /// <param name="limits">The bounds this crawl runs inside.</param>
    /// <param name="diagnostics">Where to record refused addresses.</param>
    /// <returns>The entries and the next page.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static ListingPage Parse(
        CrawledPage page,
        CrawlSelectors selectors,
        UrlPolicy policy,
        CrawlLimits limits,
        CrawlDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(selectors);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var (entries, selector) = FindEntries(page, selectors, policy, limits, diagnostics);

        return new ListingPage(entries, FindNextPage(page, selectors, policy, diagnostics), selector);
    }

    /// <summary>
    /// Finds the entries on a page, by selector or by inference.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="selectors">Selector overrides.</param>
    /// <param name="policy">What addresses are acceptable.</param>
    /// <param name="limits">The bounds this crawl runs inside.</param>
    /// <param name="diagnostics">Where to record refused addresses.</param>
    /// <returns>The entries, and the selector they were found with.</returns>
    private static (IReadOnlyList<ListingEntry> Entries, string? Selector) FindEntries(
        CrawledPage page,
        CrawlSelectors selectors,
        UrlPolicy policy,
        CrawlLimits limits,
        CrawlDiagnostics diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(selectors.Item))
        {
            // Honoured even when it finds nothing. Falling back would turn a
            // typo into a mysteriously different set of results.
            return (Collect(page, HtmlReader.QueryAll(page.Document, selectors.Item), selectors, policy, limits, diagnostics),
                selectors.Item);
        }

        // Two passes over the same candidates. The first insists on a repeated
        // block, which is what a catalogue page looks like and what makes a
        // guess safe. The second accepts a single entry, because a page with one
        // game on it is a real page — the last page of a paginated set usually
        // is — and rejecting it outright loses that game entirely.
        foreach (var required in new[] { MinimumEntries, 1 })
        {
            foreach (var candidate in ItemCandidates)
            {
                var elements = HtmlReader.QueryAll(page.Document, candidate);

                if (elements.Count < required)
                {
                    continue;
                }

                var entries = Collect(page, elements, selectors, policy, limits, diagnostics);

                if (entries.Count >= required)
                {
                    return (entries, candidate);
                }
            }
        }

        // No repeated container recognised. Falling back to the links
        // themselves, which is how a plain index page — a directory listing, a
        // hand-written list of releases — is read.
        return (FromLinks(page, policy, limits, diagnostics), null);
    }

    /// <summary>
    /// Turns candidate containers into entries.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="elements">The containers.</param>
    /// <param name="selectors">Selector overrides.</param>
    /// <param name="policy">What addresses are acceptable.</param>
    /// <param name="limits">The bounds this crawl runs inside.</param>
    /// <param name="diagnostics">Where to record refused addresses.</param>
    /// <returns>The entries that carry a usable link.</returns>
    private static List<ListingEntry> Collect(
        CrawledPage page,
        IReadOnlyList<IElement> elements,
        CrawlSelectors selectors,
        UrlPolicy policy,
        CrawlLimits limits,
        CrawlDiagnostics diagnostics)
    {
        var entries = new List<ListingEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements)
        {
            if (entries.Count >= limits.MaxLinksPerPage)
            {
                break;
            }

            var anchor = string.IsNullOrWhiteSpace(selectors.DetailLink)
                ? FindDetailAnchor(element, page.BaseAddress)
                : HtmlReader.Query(element, selectors.DetailLink);

            if (anchor?.GetAttribute("href") is not { } href)
            {
                diagnostics.ItemSkipped();
                continue;
            }

            var verdict = UrlGuard.Inspect(href, policy, page.BaseAddress);

            if (!verdict.IsAllowed)
            {
                diagnostics.LinkRejected(href, verdict.Explanation ?? "refused");
                continue;
            }

            // Same page: a "read more" pointing at itself, or an anchor.
            if (verdict.Address!.Equals(page.Address) || !seen.Add(verdict.Address.AbsoluteUri))
            {
                diagnostics.DuplicateSkipped();
                continue;
            }

            var title =
                HtmlReader.Title(page.Document, element, selectors.Title) ??
                HtmlReader.Clean(anchor.GetAttribute("title"), 400) ??
                HtmlReader.Clean(anchor.TextContent, 400);

            entries.Add(new ListingEntry(title, verdict.Address));
        }

        return entries;
    }

    /// <summary>
    /// Picks the link in a container that most likely points at the game.
    /// </summary>
    /// <param name="element">The container.</param>
    /// <param name="pageAddress">The page's own address.</param>
    /// <returns>The anchor, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A heading's link first, because that is where a title's link lives on
    /// almost every site that has headings at all. Then the container's own
    /// link, then any link with text. An image-only link is taken last: it is
    /// usually the same destination as the heading, and preferring it would lose
    /// the title that comes with the text.
    /// </remarks>
    private static IElement? FindDetailAnchor(IElement element, Uri pageAddress)
    {
        foreach (var candidate in new[]
                 {
                     "h1 a[href]", "h2 a[href]", "h3 a[href]", ".title a[href]", ".entry-title a[href]",
                     "a.title[href]", "a[href].game-link",
                 })
        {
            if (HtmlReader.Query(element, candidate) is { } heading)
            {
                return heading;
            }
        }

        // The container itself may be the link.
        if (element.LocalName.Equals("a", StringComparison.Ordinal) && element.HasAttribute("href"))
        {
            return element;
        }

        IElement? imageOnly = null;

        foreach (var anchor in HtmlReader.QueryAll(element, "a[href]"))
        {
            var href = anchor.GetAttribute("href");

            if (string.IsNullOrWhiteSpace(href) || IsNavigation(href, pageAddress))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(HtmlReader.Clean(anchor.TextContent, 400)))
            {
                return anchor;
            }

            imageOnly ??= anchor;
        }

        return imageOnly;
    }

    /// <summary>
    /// Reads a page that has no repeated container, by its links alone.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="policy">What addresses are acceptable.</param>
    /// <param name="limits">The bounds this crawl runs inside.</param>
    /// <param name="diagnostics">Where to record refused addresses.</param>
    /// <returns>The entries found.</returns>
    /// <remarks>
    /// Confined to links that go deeper than the page they are on, which is what
    /// distinguishes a list of games from the site's own navigation. Not
    /// reliable enough to be preferred, and useful enough to be the fallback:
    /// plenty of small self-hosted indexes are exactly this.
    /// </remarks>
    private static List<ListingEntry> FromLinks(
        CrawledPage page,
        UrlPolicy policy,
        CrawlLimits limits,
        CrawlDiagnostics diagnostics)
    {
        var entries = new List<ListingEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scope = HtmlReader.Query(page.Document, "main, article, .content, #content") ?? page.Document.Body;

        foreach (var anchor in HtmlReader.QueryAll(scope, "a[href]"))
        {
            // A paginator's own links pass every other test here: they are on
            // the same host, they go deeper than the page they are on, and they
            // have text. Left in, a crawl imports a game called "Next" from
            // every listing page it walks.
            if (IsInsideNavigation(anchor))
            {
                continue;
            }

            if (entries.Count >= limits.MaxLinksPerPage)
            {
                break;
            }

            var href = anchor.GetAttribute("href");

            if (string.IsNullOrWhiteSpace(href) || IsNavigation(href, page.Address))
            {
                continue;
            }

            var verdict = UrlGuard.Inspect(href, policy, page.BaseAddress);

            if (!verdict.IsAllowed)
            {
                diagnostics.LinkRejected(href, verdict.Explanation ?? "refused");
                continue;
            }

            var address = verdict.Address!;

            if (address.Equals(page.Address) ||
                !IsDeeperThan(address, page.Address) ||
                !seen.Add(address.AbsoluteUri))
            {
                continue;
            }

            var title = HtmlReader.Clean(anchor.TextContent, 400) ??
                        HtmlReader.Clean(anchor.GetAttribute("title"), 400);

            if (!string.IsNullOrWhiteSpace(title))
            {
                entries.Add(new ListingEntry(title, address));
            }
        }

        return entries;
    }

    /// <summary>
    /// Finds the next listing page.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="selectors">Selector overrides.</param>
    /// <param name="policy">What addresses are acceptable.</param>
    /// <param name="diagnostics">Where to record refused addresses.</param>
    /// <returns>The next page, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <c>rel="next"</c> first: it exists for exactly this and means exactly
    /// this. The class names after it are conventions, and the word match last,
    /// because "Next" in link text is the least reliable of the three and still
    /// the only thing many sites offer.
    /// </remarks>
    private static Uri? FindNextPage(
        CrawledPage page,
        CrawlSelectors selectors,
        UrlPolicy policy,
        CrawlDiagnostics diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(selectors.NextPage))
        {
            return Resolve(HtmlReader.Query(page.Document, selectors.NextPage), page, policy, diagnostics);
        }

        foreach (var candidate in NextCandidates)
        {
            if (Resolve(HtmlReader.Query(page.Document, candidate), page, policy, diagnostics) is { } found)
            {
                return found;
            }
        }

        foreach (var anchor in HtmlReader.QueryAll(page.Document, ".pagination a[href], .nav-links a[href], nav a[href]"))
        {
            var text = HtmlReader.Clean(anchor.TextContent, 32)?.ToLowerInvariant();

            if (text is not null &&
                NextWords.Contains(text, StringComparer.Ordinal) &&
                Resolve(anchor, page, policy, diagnostics) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Resolves an anchor's target against the page.</summary>
    /// <param name="element">The anchor or link element.</param>
    /// <param name="page">The page it is on.</param>
    /// <param name="policy">What addresses are acceptable.</param>
    /// <param name="diagnostics">Where to record refused addresses.</param>
    /// <returns>The address, or <see langword="null"/>.</returns>
    private static Uri? Resolve(
        IElement? element,
        CrawledPage page,
        UrlPolicy policy,
        CrawlDiagnostics diagnostics)
    {
        if (element?.GetAttribute("href") is not { } href || string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var verdict = UrlGuard.Inspect(href, policy, page.BaseAddress);

        if (verdict.IsAllowed)
        {
            // A next link pointing at the current page is how a paginator
            // announces the last page on some themes.
            return verdict.Address!.Equals(page.Address) ? null : verdict.Address;
        }

        diagnostics.LinkRejected(href, verdict.Explanation ?? "refused");

        return null;
    }

    /// <summary>
    /// Determines whether a link sits inside the site's own furniture.
    /// </summary>
    /// <param name="anchor">The link.</param>
    /// <returns><see langword="true"/> when it is navigation rather than content.</returns>
    /// <remarks>
    /// Walks up a bounded number of levels rather than the whole tree: a page
    /// whose entire body is wrapped in something called "nav" would otherwise
    /// yield nothing at all, and that is a worse failure than importing one
    /// stray link.
    /// </remarks>
    private static bool IsInsideNavigation(IElement anchor)
    {
        var current = anchor.ParentElement;

        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            if (current.LocalName is "nav" or "header" or "footer" or "aside")
            {
                return true;
            }

            var className = current.ClassName;

            if (className is not null &&
                (className.Contains("pagination", StringComparison.OrdinalIgnoreCase) ||
                 className.Contains("nav-links", StringComparison.OrdinalIgnoreCase) ||
                 className.Contains("breadcrumb", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            current = current.ParentElement;
        }

        return false;
    }

    /// <summary>Determines whether an href is obviously site furniture.</summary>
    /// <param name="href">The address as written.</param>
    /// <param name="pageAddress">The page's own address.</param>
    /// <returns><see langword="true"/> when it should be ignored.</returns>
    private static bool IsNavigation(string href, Uri pageAddress)
    {
        var trimmed = href.Trim();

        if (trimmed.Length == 0 ||
            trimmed.StartsWith('#') ||
            trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Feeds, logins and the paginator itself are not games.
        foreach (var fragment in new[]
                 {
                     "/feed", "/rss", "/login", "/register", "/wp-login", "/cart", "/account",
                     "/privacy", "/terms", "/contact", "/about", "?share=", "?replytocom=",
                 })
        {
            if (trimmed.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether one address is below another in the same site.
    /// </summary>
    /// <param name="candidate">The link's target.</param>
    /// <param name="pageAddress">The page it was found on.</param>
    /// <returns><see langword="true"/> when it goes deeper.</returns>
    private static bool IsDeeperThan(Uri candidate, Uri pageAddress)
    {
        if (!candidate.Host.Equals(pageAddress.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var from = pageAddress.AbsolutePath.TrimEnd('/');
        var to = candidate.AbsolutePath.TrimEnd('/');

        return to.Length > from.Length &&
               to.StartsWith(from, StringComparison.OrdinalIgnoreCase);
    }
}
