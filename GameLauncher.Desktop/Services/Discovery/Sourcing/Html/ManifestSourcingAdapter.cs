using System.IO;
using System.Net.Http;
using System.Text.Json;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Crawling;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Html;

/// <summary>
/// Resolves a listing to a download using a manifest's <c>sourcing</c> section.
/// </summary>
/// <remarks>
/// <para>
/// The generic half of download resolution: three strategies, chosen per
/// manifest, none of which knows anything about a particular site.
/// </para>
/// <list type="bullet">
/// <item><c>direct-link</c> reads the addresses off the game's own page.</item>
/// <item><c>mapped-field</c> takes the address the catalogue already recorded.</item>
/// <item><c>external-script</c> asks a program the user nominated.</item>
/// </list>
/// <para>
/// None of them transfers anything. This produces addresses and the facts
/// published about them; the existing download stack does the rest, which is why
/// a checksum found on a page is mapped onto the same fields every other source
/// uses rather than verified here.
/// </para>
/// <para>
/// It runs at install time, for the one game being installed. That is what
/// <c>resolution: lazy</c> means and why it is the default: resolving a
/// catalogue of several thousand games during import would cost several thousand
/// page fetches to answer a question about the one game somebody eventually
/// clicks, and the answers would be stale by the time they did.
/// </para>
/// </remarks>
public sealed class ManifestSourcingAdapter : ISourcingAdapter
{
    /// <summary>Dispatch key this adapter reports.</summary>
    public const string AdapterKey = "manifest-sourcing";

    /// <summary>Largest script response accepted, in characters.</summary>
    /// <remarks>
    /// A hook that writes without end would otherwise be read without end. A
    /// resolution answer is a handful of addresses; this is generous for that
    /// and finite, which is the property that matters.
    /// </remarks>
    private const int MaxScriptOutput = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IFeedManifestStore _manifests;
    private readonly IScriptHookRunner _hooks;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRobotsPolicy _robots;
    private readonly ILogger<ManifestSourcingAdapter> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="manifests">Supplies the user's manifests.</param>
    /// <param name="hooks">Runs an external resolver, when one is named.</param>
    /// <param name="httpClientFactory">Supplies the configured page client.</param>
    /// <param name="robots">Checks each site's published rules before fetching.</param>
    /// <param name="logger">Logger for sourcing diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ManifestSourcingAdapter(
        IFeedManifestStore manifests,
        IScriptHookRunner hooks,
        IHttpClientFactory httpClientFactory,
        IRobotsPolicy robots,
        ILogger<ManifestSourcingAdapter> logger)
    {
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _robots = robots ?? throw new ArgumentNullException(nameof(robots));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => AdapterKey;

    /// <inheritdoc />
    public string DisplayName => "Manifest sourcing";

    /// <summary>
    /// Ranked from the manifests, ahead of the built-ins by default.
    /// </summary>
    /// <remarks>
    /// The highest priority any loaded manifest asks for, because one adapter
    /// serves them all and the resolver ranks adapters rather than manifests.
    /// The payload each resolution returns carries its own manifest's number, so
    /// the merged mirror list is still ordered per manifest.
    /// </remarks>
    public int Priority =>
        _manifests.Cached is { } cached && cached.Count > 0
            ? cached.Where(manifest => manifest.ProvidesSourcing)
                .Select(manifest => manifest.SourcingPriority)
                .DefaultIfEmpty(100)
                .Max()
            : 100;

    /// <inheritdoc />
    /// <remarks>
    /// Guesses yes before the adapter folder has been read, for the same reason
    /// the scriptable adapter does: this is synchronous, the answer lives on
    /// disk, and on the install path a wrong yes costs one call that declines
    /// whereas a wrong no costs the download.
    /// </remarks>
    public bool CanHandle(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return _manifests.Cached is not { } loaded || Find(loaded, parsed) is not null;
    }

    /// <inheritdoc />
    public bool DefinitelyHandles(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        _manifests.Cached is { } loaded &&
        Find(loaded, parsed) is not null;

    /// <inheritdoc />
    public async Task<SourcingPayload> ExtractDownloadPayloadAsync(
        CatalogListing listing,
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var page))
        {
            return SourcingPayload.Unsupported;
        }

        var manifests = await _manifests.GetAsync(cancellationToken).ConfigureAwait(false);
        var manifest = Find(manifests, page);

        if (manifest is null)
        {
            return SourcingPayload.Unsupported;
        }

        var sourcing = manifest.Sourcing!;

        try
        {
            var candidates = await ResolveAsync(manifest, sourcing, listing, page, cancellationToken)
                .ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                return new SourcingPayload(
                    [],
                    SourcingRefusal.NoPayload,
                    $"The '{Name(manifest)}' source described no usable download for '{listing.Title}'.",
                    sourcing.Priority);
            }

            // Highest priority first, so a page's own ordering and a strategy's
            // opinion both survive into the mirror list the installer walks.
            var downloads = candidates
                .OrderByDescending(candidate => candidate.Priority)
                .Select((candidate, index) =>
                    candidate.ToDownload(listing.ListingId, manifest.Key, index))
                .ToArray();

            _logger.LogInformation(
                "Source '{Key}' resolved {Count} address(es) for '{Title}' at priority {Priority}.",
                manifest.Key, downloads.Length, listing.Title, sourcing.Priority);

            return new SourcingPayload(downloads, Priority: sourcing.Priority);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or
                                       FormatException or JsonException or InvalidOperationException)
        {
            // Named, because with several manifests loaded "a source failed" is
            // not something anyone can act on and "home-nas failed" says which
            // file to open.
            _logger.LogWarning(ex, "Source '{Key}' failed for '{Title}'.", manifest.Key, listing.Title);

            return new SourcingPayload(
                [],
                SourcingRefusal.Unreachable,
                $"The '{Name(manifest)}' source could not supply a download: {ex.Message}",
                sourcing.Priority);
        }
    }

    /// <summary>
    /// Runs whichever strategy the manifest declared.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="sourcing">Its sourcing section.</param>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="page">The page that matched.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The candidates found.</returns>
    private Task<IReadOnlyList<DownloadCandidate>> ResolveAsync(
        FeedManifest manifest,
        FeedSourcing sourcing,
        CatalogListing listing,
        Uri page,
        CancellationToken cancellationToken) => sourcing.Strategy switch
    {
        SourcingStrategy.MappedField => Task.FromResult(FromCatalog(listing, sourcing, page)),
        SourcingStrategy.ExternalScript => FromScriptAsync(manifest, sourcing, listing, page, cancellationToken),
        _ => FromPageAsync(sourcing, page, cancellationToken)
    };

    /// <summary>
    /// Reads candidates off the game's own page.
    /// </summary>
    /// <param name="sourcing">The sourcing section.</param>
    /// <param name="page">The page to read.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The candidates found.</returns>
    private async Task<IReadOnlyList<DownloadCandidate>> FromPageAsync(
        FeedSourcing sourcing,
        Uri page,
        CancellationToken cancellationToken)
    {
        var fetcher = new PageFetcher(_httpClientFactory, _robots, _logger);
        var diagnostics = new CrawlDiagnostics();

        // The page itself must be fetchable under the ordinary rules. Resolution
        // is a network read like any other and gets no exemption from robots.txt
        // or the address policy.
        var policy = sourcing.ToPolicy(page.Host) with { Schemes = ["http", "https"] };

        using var result = await fetcher
            .FetchAsync(page, policy, CrawlLimits.Default, diagnostics, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsOk)
        {
            throw new InvalidOperationException(
                result.Explanation ?? $"{page} could not be read.");
        }

        return DirectLinkExtractor.Extract(result.Page!, sourcing, diagnostics);
    }

    /// <summary>
    /// Takes the addresses the catalogue already recorded.
    /// </summary>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="sourcing">The sourcing section.</param>
    /// <param name="page">The page that matched.</param>
    /// <returns>The candidates, re-checked against the address policy.</returns>
    /// <remarks>
    /// Nothing is fetched: the answer is already in hand, because the catalogue
    /// half emitted it. The addresses are vetted again all the same — they were
    /// stored from a feed or a script and this is the gate that decides what
    /// reaches the download stack, so trusting the database to have been careful
    /// would put the check in the wrong place.
    /// </remarks>
    private static IReadOnlyList<DownloadCandidate> FromCatalog(
        CatalogListing listing,
        FeedSourcing sourcing,
        Uri page)
    {
        var policy = sourcing.ToPolicy(page.Host);
        var candidates = new List<DownloadCandidate>();

        foreach (var download in listing.Downloads)
        {
            var verdict = UrlGuard.Inspect(download.Url, policy);

            if (!verdict.IsAllowed)
            {
                continue;
            }

            candidates.Add(new DownloadCandidate
            {
                Address = verdict.Address!,
                SourcePage = page,
                FileName = download.FileName,
                SizeBytes = download.SizeBytes,
                Sha256 = download.Sha256,
                Sha1 = download.Sha1,
                Md5 = download.Md5,
                Format = download.Format,

                // The recorded order is the preference the catalogue already
                // worked out, so it is preserved rather than recomputed.
                Priority = int.MaxValue - download.MirrorRank,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["strategy"] = "mapped-field"
                }
            });
        }

        return candidates;
    }

    /// <summary>
    /// Asks an external program where a game can be fetched from.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="sourcing">Its sourcing section.</param>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="page">The page that matched.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The candidates the program returned.</returns>
    /// <remarks>
    /// <para>
    /// The contract is a pipe: a JSON description of the listing on standard
    /// input, a JSON list of candidates on standard output, exit zero. That is
    /// documented in <c>docs/generic-crawler.md</c> and deliberately small
    /// enough to implement in any language.
    /// </para>
    /// <para>
    /// A script's answer is checked exactly as a page's is. It runs as a program
    /// the user chose, which is a good reason to let it do the resolving and no
    /// reason at all to let it nominate an address the address policy would have
    /// refused.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<DownloadCandidate>> FromScriptAsync(
        FeedManifest manifest,
        FeedSourcing sourcing,
        CatalogListing listing,
        Uri page,
        CancellationToken cancellationToken)
    {
        var script = sourcing.Script
                     ?? throw new InvalidOperationException(
                         $"'{manifest.Key}' declares external-script sourcing but names no script.");

        var request = JsonSerializer.Serialize(
            new ScriptRequest(
                listing.ListingId,
                listing.Title,
                listing.Year,
                page.AbsoluteUri,
                manifest.Key),
            JsonOptions);

        var directory = Path.GetDirectoryName(manifest.SourcePath) ?? ".";

        var output = await _hooks
            .RunAsync(script, request, directory, cancellationToken)
            .ConfigureAwait(false);

        if (output.Length > MaxScriptOutput)
        {
            throw new InvalidOperationException(
                $"The resolver for '{manifest.Key}' wrote {output.Length} characters, " +
                $"more than the {MaxScriptOutput} allowed.");
        }

        var parsed = JsonSerializer.Deserialize<ScriptResponse>(output, JsonOptions);

        if (parsed?.Candidates is null)
        {
            return [];
        }

        var policy = sourcing.ToPolicy(page.Host);
        var candidates = new List<DownloadCandidate>();

        foreach (var candidate in parsed.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Url))
            {
                continue;
            }

            var verdict = UrlGuard.Inspect(candidate.Url, policy);

            if (!verdict.IsAllowed)
            {
                _logger.LogDebug(
                    "Resolver for '{Key}' offered {Url}, which was refused: {Reason}.",
                    manifest.Key, candidate.Url, verdict.Explanation);

                continue;
            }

            candidates.Add(new DownloadCandidate
            {
                Address = verdict.Address!,
                SourcePage = page,
                FileName = candidate.FileName,
                SizeBytes = candidate.SizeBytes is > 0 ? candidate.SizeBytes : null,
                Sha256 = HexDigest.Clean(candidate.Sha256),
                Sha1 = HexDigest.Clean(candidate.Sha1),
                Md5 = HexDigest.Clean(candidate.Md5),
                MimeType = candidate.MimeType,
                Format = candidate.Format,
                Priority = candidate.Priority,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["strategy"] = "external-script"
                }
            });
        }

        return candidates;
    }

    /// <summary>What a manifest to call this address is, if any.</summary>
    /// <param name="manifests">The loaded manifests.</param>
    /// <param name="address">The page address.</param>
    /// <returns>The manifest, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Highest priority wins, and file-name order breaks a tie, so two manifests
    /// claiming one host resolve predictably and the number is how someone says
    /// which they meant.
    /// </remarks>
    private static FeedManifest? Find(IReadOnlyList<FeedManifest> manifests, Uri address) =>
        manifests
            .Where(manifest => manifest.ProvidesSourcing && Claims(manifest, address))
            .OrderByDescending(manifest => manifest.SourcingPriority)
            .ThenBy(manifest => manifest.SourcePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>Determines whether one manifest claims an address.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    /// <remarks>
    /// The <c>match</c> section when the manifest has one, and otherwise the
    /// host its own crawl starts from — so a manifest that crawls a site is
    /// understood to source from it too, without having to say so twice.
    /// </remarks>
    private static bool Claims(FeedManifest manifest, Uri address)
    {
        var hosts = manifest.Match.Hosts.Count > 0
            ? manifest.Match.Hosts
            : CrawlHost(manifest) is { } inferred ? [inferred] : (IReadOnlyList<string>)[];

        if (hosts.Count == 0)
        {
            return false;
        }

        var host = address.Host;

        var matched = hosts.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            (host.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
             host.EndsWith("." + candidate.TrimStart('.'), StringComparison.OrdinalIgnoreCase)));

        if (!matched)
        {
            return false;
        }

        return manifest.Match.PathContains.Count == 0 ||
               manifest.Match.PathContains.Any(fragment =>
                   address.AbsoluteUri.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the host a manifest's crawl starts from.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The host, or <see langword="null"/>.</returns>
    private static string? CrawlHost(FeedManifest manifest) =>
        manifest.Crawler is { } crawler && UrlGuard.Canonicalize(crawler.Url) is { } address
            ? address.Host
            : null;

    /// <summary>What to call a manifest when telling someone what happened.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>Its display name, or its key.</returns>
    private static string Name(FeedManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.Key : manifest.DisplayName;

    /// <summary>What an external resolver is told.</summary>
    /// <param name="ListingId">The listing being installed.</param>
    /// <param name="Title">Its title.</param>
    /// <param name="Year">Its year, when known.</param>
    /// <param name="SourceUrl">The page to resolve.</param>
    /// <param name="SourceKey">The manifest asking.</param>
    private sealed record ScriptRequest(
        string ListingId,
        string Title,
        int? Year,
        string SourceUrl,
        string SourceKey);

    /// <summary>What an external resolver returns.</summary>
    /// <param name="Candidates">The addresses it found.</param>
    private sealed record ScriptResponse(List<ScriptCandidate>? Candidates);

    /// <summary>One address an external resolver offered.</summary>
    private sealed record ScriptCandidate
    {
        /// <summary>The address. Required.</summary>
        public string? Url { get; init; }

        /// <summary>The file's name, when known.</summary>
        public string? FileName { get; init; }

        /// <summary>The file's size in bytes, when known.</summary>
        public long? SizeBytes { get; init; }

        /// <summary>A published SHA-256.</summary>
        public string? Sha256 { get; init; }

        /// <summary>A published SHA-1.</summary>
        public string? Sha1 { get; init; }

        /// <summary>A published MD5.</summary>
        public string? Md5 { get; init; }

        /// <summary>A published media type.</summary>
        public string? MimeType { get; init; }

        /// <summary>A format label.</summary>
        public string? Format { get; init; }

        /// <summary>Where this ranks against its siblings; higher first.</summary>
        public int Priority { get; init; }
    }
}
