using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using GameLauncher.Desktop.Services.Discovery.Crawling;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Html;

/// <summary>
/// Reads download candidates off a game's own page.
/// </summary>
/// <remarks>
/// <para>
/// The <c>direct-link</c> strategy. It looks for what a release page normally
/// has on it — a link to a file, sometimes with a checksum and a size printed
/// beside it — and turns each into a candidate. It follows nothing, submits
/// nothing, and fetches nothing itself: it is handed a parsed page and returns
/// what the page says.
/// </para>
/// <para>
/// Every address it produces goes through <see cref="UrlGuard"/> first, because
/// the page it came from was written by somebody else. A link is the cheapest
/// way for a stranger to nominate what a program should fetch, and the whole
/// value of a single gate is that a strategy cannot forget to use it.
/// </para>
/// </remarks>
public static partial class DirectLinkExtractor
{
    /// <summary>Most candidates to take from one page.</summary>
    /// <remarks>
    /// A page with a hundred links on it is a page whose selector is wrong, and
    /// queueing a hundred mirrors would make the install stack pay for that
    /// mistake one timeout at a time.
    /// </remarks>
    private const int MaxCandidates = 12;

    /// <summary>Extensions that look like a game rather than a document.</summary>
    private static readonly string[] ArchiveExtensions =
    [
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso",
        ".exe", ".msi", ".dmg", ".img", ".dsk", ".d64", ".adf", ".love", ".apk", ".torrent",
    ];

    /// <summary>Words a download link tends to use when it has no file extension.</summary>
    private static readonly string[] DownloadWords =
        ["download", "get it", "get the", "mirror", "direct link", "grab"];

    [GeneratedRegex(
        @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>[KMGT]i?B|bytes?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"\b(?<digest>[0-9a-fA-F]{32}|[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    /// <summary>
    /// Reads the candidates a page offers.
    /// </summary>
    /// <param name="page">The page to read.</param>
    /// <param name="sourcing">The manifest's sourcing section.</param>
    /// <param name="diagnostics">Where to record refused addresses.</param>
    /// <returns>The candidates, best first.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<DownloadCandidate> Extract(
        CrawledPage page,
        FeedSourcing sourcing,
        CrawlDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(sourcing);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var policy = sourcing.ToPolicy(page.Address.Host);
        var selectors = sourcing.Selectors;

        // Page-wide values, read once. A release page usually prints one
        // checksum and one size for the one file it offers, outside the link.
        var pageChecksum = ReadDigest(page.Document, selectors.Checksum);
        var pageSize = ReadSize(page.Document, selectors.Size);

        var anchors = string.IsNullOrWhiteSpace(selectors.DownloadLink)
            ? Infer(page)
            : HtmlReader.QueryAll(page.Document, selectors.DownloadLink);

        var candidates = new List<DownloadCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in anchors)
        {
            if (candidates.Count >= MaxCandidates)
            {
                break;
            }

            var href = anchor.GetAttribute("href");

            if (string.IsNullOrWhiteSpace(href))
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

            if (!seen.Add(address.AbsoluteUri))
            {
                continue;
            }

            // Values printed beside this particular link beat the page-wide
            // ones: a page offering two files prints each one's checksum next
            // to it, and taking the first for both would be worse than taking
            // neither.
            var nearby = NearestContainer(anchor);

            candidates.Add(new DownloadCandidate
            {
                Address = address,
                SourcePage = page.Address,
                FileName = HtmlReader.Text(nearby, selectors.FileName),
                SizeBytes = ReadSize(nearby, selectors.Size) ?? pageSize,
                Sha256 = Pick(nearby, selectors.Sha256, 64) ?? Match(pageChecksum, 64),
                Sha1 = Pick(nearby, selectors.Sha1, 40) ?? Match(pageChecksum, 40),
                Md5 = Pick(nearby, selectors.Md5, 32) ?? Match(pageChecksum, 32),
                Format = FormatOf(address),

                // Earlier on the page is tried earlier. A page listing mirrors
                // is stating a preference by the order it lists them in, and
                // that is the only preference it states.
                Priority = MaxCandidates - candidates.Count,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["strategy"] = "direct-link",
                    ["page"] = page.Address.AbsoluteUri
                }
            });
        }

        return candidates;
    }

    /// <summary>
    /// Finds the links on a page that look like downloads.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <returns>The anchors worth considering, in document order.</returns>
    /// <remarks>
    /// An extension that names an archive first, because that is nearly always
    /// right and never needs a word list. Then links whose text says what they
    /// are, which catches the many sites that serve files from a script with no
    /// extension in the address at all.
    /// </remarks>
    private static IReadOnlyList<IElement> Infer(CrawledPage page)
    {
        var byExtension = new List<IElement>();
        var byWording = new List<IElement>();

        var scope = HtmlReader.Query(page.Document, "main, article, .entry-content, .post-content, #content")
                    ?? page.Document.Body;

        foreach (var anchor in HtmlReader.QueryAll(scope, "a[href]"))
        {
            var href = anchor.GetAttribute("href");

            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            if (LooksLikeFile(href))
            {
                byExtension.Add(anchor);
                continue;
            }

            var text = HtmlReader.Clean(anchor.TextContent, 120)?.ToLowerInvariant();
            var rel = anchor.GetAttribute("rel");

            if ((text is not null && DownloadWords.Any(word => text.Contains(word, StringComparison.Ordinal))) ||
                anchor.HasAttribute("download") ||
                (rel is not null && rel.Contains("nofollow", StringComparison.OrdinalIgnoreCase) is false &&
                 anchor.ClassName?.Contains("download", StringComparison.OrdinalIgnoreCase) == true))
            {
                byWording.Add(anchor);
            }
        }

        // Extensions first, wording after, because the first group is evidence
        // and the second is a guess.
        return [.. byExtension, .. byWording];
    }

    /// <summary>Determines whether an address names a file worth fetching.</summary>
    /// <param name="href">The address as written.</param>
    /// <returns><see langword="true"/> when it looks like a file.</returns>
    private static bool LooksLikeFile(string href)
    {
        if (href.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The query is dropped before looking at the extension, so
        // 'game.zip?token=1' is still recognised as a zip.
        var path = href.Split('?', '#')[0];

        return ArchiveExtensions.Any(extension =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads a format label from an address.</summary>
    /// <param name="address">The download address.</param>
    /// <returns>The label, or <see langword="null"/>.</returns>
    private static string? FormatOf(Uri address)
    {
        if (address.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            return "Torrent";
        }

        var path = address.AbsolutePath;
        var dot = path.LastIndexOf('.');

        if (dot < 0 || dot == path.Length - 1)
        {
            return null;
        }

        var extension = path[(dot + 1)..];

        return extension.Length is > 0 and <= 5 && extension.All(char.IsLetterOrDigit)
            ? extension.ToUpperInvariant()
            : null;
    }

    /// <summary>
    /// Finds the block a link sits in, for reading values printed beside it.
    /// </summary>
    /// <param name="anchor">The link.</param>
    /// <returns>The nearest enclosing block, or the link itself.</returns>
    /// <remarks>
    /// A list item, table row or paragraph is where a site puts "game.zip — 1.8
    /// GB — sha256: …". Walking up more than a few levels would reach the whole
    /// page and read another file's numbers.
    /// </remarks>
    private static IElement NearestContainer(IElement anchor)
    {
        var current = anchor.ParentElement;

        for (var depth = 0; depth < 3 && current is not null; depth++)
        {
            if (current.LocalName is "li" or "tr" or "p" or "dd" or "td")
            {
                return current;
            }

            current = current.ParentElement;
        }

        return anchor.ParentElement ?? anchor;
    }

    /// <summary>Reads a digest of a particular length from a scope.</summary>
    /// <param name="scope">Where to look.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <param name="length">How many hex characters the digest must have.</param>
    /// <returns>The digest, or <see langword="null"/>.</returns>
    private static string? Pick(IParentNode? scope, string? selector, int length)
    {
        if (scope is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            return Match(HexDigest.Clean(HtmlReader.Query(scope, selector)?.TextContent), length);
        }

        return Match(ReadDigest(scope, null), length);
    }

    /// <summary>
    /// Finds a digest in a scope's text.
    /// </summary>
    /// <param name="scope">Where to look.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <returns>The digest, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Sites print checksums in a dozen layouts and label them inconsistently,
    /// so a run of hex of the right length is a more reliable signal than any
    /// label. The length is what says which algorithm it is.
    /// </remarks>
    private static string? ReadDigest(IParentNode? scope, string? selector)
    {
        if (scope is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            return HexDigest.Clean(HtmlReader.Query(scope, selector)?.TextContent);
        }

        foreach (var candidate in new[] { "code", ".checksum", ".sha256", ".sha1", ".md5", "samp", "kbd" })
        {
            foreach (var element in HtmlReader.QueryAll(scope, candidate))
            {
                if (HexDigest.Clean(element.TextContent) is { } found)
                {
                    return found;
                }
            }
        }

        var text = scope is IElement element2 ? element2.TextContent : null;

        if (string.IsNullOrWhiteSpace(text) || text.Length > 20_000)
        {
            return null;
        }

        var match = DigestPattern().Match(text);

        return match.Success ? match.Groups["digest"].Value.ToLowerInvariant() : null;
    }

    /// <summary>Keeps a digest only when it is the requested length.</summary>
    /// <param name="digest">The digest.</param>
    /// <param name="length">The length required.</param>
    /// <returns>The digest, or <see langword="null"/>.</returns>
    private static string? Match(string? digest, int length) =>
        digest is not null && digest.Length == length ? digest : null;

    /// <summary>
    /// Reads a printed file size.
    /// </summary>
    /// <param name="scope">Where to look.</param>
    /// <param name="selector">A selector the manifest named, or <see langword="null"/>.</param>
    /// <returns>The size in bytes, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Decimal units, because that is what a site means when it prints "1.8 GB"
    /// next to a download. Only ever a hint: the transfer reports the real
    /// length, and this is what a progress bar has to work with until it does.
    /// </remarks>
    private static long? ReadSize(IParentNode? scope, string? selector)
    {
        if (scope is null)
        {
            return null;
        }

        string? text;

        if (!string.IsNullOrWhiteSpace(selector))
        {
            text = HtmlReader.Query(scope, selector)?.TextContent;
        }
        else
        {
            text = HtmlReader.Query(scope, ".size, .filesize, .file-size")?.TextContent
                   ?? (scope is IElement element ? element.TextContent : null);
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > 20_000)
        {
            return null;
        }

        var match = SizePattern().Match(text);

        if (!match.Success ||
            !double.TryParse(
                match.Groups["value"].Value.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return null;
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant();

        var multiplier = unit switch
        {
            "kb" => 1_000L,
            "mb" => 1_000_000L,
            "gb" => 1_000_000_000L,
            "tb" => 1_000_000_000_000L,
            "kib" => 1024L,
            "mib" => 1024L * 1024,
            "gib" => 1024L * 1024 * 1024,
            "tib" => 1024L * 1024 * 1024 * 1024,
            _ => 1L
        };

        var bytes = (long)(amount * multiplier);

        return bytes > 0 ? bytes : null;
    }
}
