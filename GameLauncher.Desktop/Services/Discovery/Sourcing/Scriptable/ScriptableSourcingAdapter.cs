using System.Globalization;
using System.IO;
using System.Net.Http;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Http;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// Sourcing adapter driven by user-supplied feed manifests.
/// </summary>
/// <remarks>
/// <para>
/// One adapter, any number of feeds. A manifest dropped into the adapter
/// directory adds a source without a class, a registration or a rebuild, which
/// is the point: the feeds worth having are the ones nobody here has heard of —
/// a home server, a preservation project's export, a curated list a community
/// maintains.
/// </para>
/// <para>
/// It obeys the same rules the built-in adapters do. <c>robots.txt</c> is
/// checked before any HTTP fetch, exactly as it is for the sources this launcher
/// ships. An extension point that quietly dropped that would not be an extension
/// point; it would be a way around a decision the rest of the code takes
/// seriously, and the first thing anyone would use it for.
/// </para>
/// </remarks>
public sealed class ScriptableSourcingAdapter : ISourcingAdapter
{
    /// <summary>Dispatch key this adapter reports.</summary>
    /// <remarks>
    /// A single key for the whole family. Individual manifests carry their own,
    /// which is what lands on the download rows, so a person can still see which
    /// feed supplied a file.
    /// </remarks>
    public const string AdapterKey = "scriptable-feed";

    /// <summary>Name of the configured <see cref="HttpClient"/> used for feed requests.</summary>
    public const string HttpClientName = "sourcing-scriptable-feed";

    private readonly IFeedManifestStore _manifests;
    private readonly IScriptHookRunner _hooks;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRobotsPolicy _robots;
    private readonly ILogger<ScriptableSourcingAdapter> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="manifests">Supplies the user's feed manifests.</param>
    /// <param name="hooks">Runs a manifest's transform program, when it has one.</param>
    /// <param name="httpClientFactory">Supplies the configured feed client.</param>
    /// <param name="robots">Checks each site's published rules before fetching.</param>
    /// <param name="logger">Logger for sourcing diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ScriptableSourcingAdapter(
        IFeedManifestStore manifests,
        IScriptHookRunner hooks,
        IHttpClientFactory httpClientFactory,
        IRobotsPolicy robots,
        ILogger<ScriptableSourcingAdapter> logger)
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
    public string DisplayName => "Custom feeds";

    /// <summary>
    /// Determines whether any manifest claims an address.
    /// </summary>
    /// <param name="url">The page address.</param>
    /// <returns><see langword="true"/> when one does.</returns>
    /// <remarks>
    /// Synchronous by interface, and reading the manifest folder is not. Already
    /// loaded manifests give a real answer; before the first load it says yes.
    /// A wrong yes costs one asynchronous call that returns
    /// <see cref="SourcingRefusal.Unsupported"/>, whereas a wrong no would make
    /// the first install after a restart silently skip every custom feed.
    /// </remarks>
    public bool CanHandle(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return _manifests.Cached is not { } loaded || Find(loaded, parsed) is not null;
    }

    /// <summary>
    /// Answers only from manifests already read, never from the guess above.
    /// </summary>
    /// <param name="url">The page address.</param>
    /// <returns><see langword="true"/> only when a loaded manifest claims it.</returns>
    /// <remarks>
    /// Before the folder has been read this says no, which is the safe direction
    /// for the callers that use it. Advertising every listing as installable
    /// because the manifests happened not to be loaded yet would be exactly the
    /// wrong way to be wrong.
    /// </remarks>
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

        try
        {
            return await ResolveAsync(manifest, listing, page, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or
                                       FormatException or InvalidOperationException)
        {
            // Named in the message. With several manifests loaded, "a feed
            // failed" is not something a person can act on; "home-nas failed"
            // tells them which file to open.
            _logger.LogWarning(ex, "Feed '{Key}' failed for '{Title}'.", manifest.Key, listing.Title);

            return new SourcingPayload(
                [],
                SourcingRefusal.Unreachable,
                $"The '{Name(manifest)}' feed could not supply a download: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches, transforms and maps one manifest's payload.
    /// </summary>
    /// <param name="manifest">The manifest to run.</param>
    /// <param name="listing">The listing being installed.</param>
    /// <param name="page">The address that matched.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The payload, or a refusal.</returns>
    private async Task<SourcingPayload> ResolveAsync(
        FeedManifest manifest,
        CatalogListing listing,
        Uri page,
        CancellationToken cancellationToken)
    {
        var request = Substitute(manifest.Request.Url, listing, page);

        var (text, refusal) = await FetchAsync(manifest, request, cancellationToken).ConfigureAwait(false);

        if (refusal is not null)
        {
            return refusal;
        }

        var format = manifest.Format;

        if (manifest.Transform is { } transform)
        {
            text = await _hooks
                .RunAsync(transform, text!, Path.GetDirectoryName(manifest.SourcePath) ?? ".", cancellationToken)
                .ConfigureAwait(false);

            // A hook's contract is JSON out, whatever went in. Letting it also
            // choose a format would mean a manifest declaring one thing and its
            // hook another, with nothing to reconcile them.
            format = FeedFormat.Json;
        }

        var downloads = FeedDownloadMapper.Map(FeedReader.Read(text!, format), manifest, listing.ListingId);

        if (downloads.Count == 0)
        {
            return new SourcingPayload(
                [],
                SourcingRefusal.NoPayload,
                $"The '{Name(manifest)}' feed answered, but described no usable download.");
        }

        _logger.LogInformation(
            "Feed '{Key}' supplied {Count} address(es) for '{Title}' at priority {Priority}.",
            manifest.Key, downloads.Count, listing.Title, manifest.Priority);

        // The manifest's own number, not this adapter's. One adapter serves every
        // feed in the folder, and they do not agree about where they belong.
        return new SourcingPayload(downloads, Priority: manifest.Priority);
    }

    /// <summary>
    /// Reads a manifest's payload, from the network or from beside the manifest.
    /// </summary>
    /// <param name="manifest">The manifest being run.</param>
    /// <param name="request">The resolved request address.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The payload text, or the refusal that stopped it.</returns>
    /// <remarks>
    /// A request with no scheme names a file in the adapter directory, which is
    /// what makes a purely local catalogue possible: a manifest and a JSON file
    /// beside it, no server involved.
    /// </remarks>
    private async Task<(string? Text, SourcingPayload? Refusal)> FetchAsync(
        FeedManifest manifest,
        string request,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            return (await ReadLocalAsync(manifest, request, cancellationToken).ConfigureAwait(false), null);
        }

        // The same gate every other network read in this application passes
        // through. A manifest is a user's instruction to this launcher, not a
        // dispensation from the site's.
        if (!await _robots.IsAllowedAsync(address, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Feed '{Key}' is disallowed by robots.txt at {Address}.", manifest.Key, address);

            return (null, new SourcingPayload(
                [],
                SourcingRefusal.DisallowedByRobots,
                $"{address.Host} does not permit automated requests to that path, " +
                $"so the '{Name(manifest)}' feed was not fetched."));
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, address);

        foreach (var (name, value) in manifest.Request.Headers)
        {
            // TryAdd rather than Add: a manifest naming a header the client
            // already sets should not throw an exception a person cannot debug
            // from a YAML file.
            message.Headers.TryAddWithoutValidation(name, value);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), null);
    }

    /// <summary>
    /// Reads a payload file from the adapter directory.
    /// </summary>
    /// <param name="manifest">The manifest naming it.</param>
    /// <param name="request">The file name or relative path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file's contents.</returns>
    /// <exception cref="InvalidOperationException">The path escapes the adapter directory, or is missing.</exception>
    /// <remarks>
    /// Confined to the folder the manifest came from. Without that check a
    /// manifest could name <c>../../../../Windows/win.ini</c> and have the
    /// launcher read it — a local file disclosure dressed as a feed.
    /// </remarks>
    private static async Task<string> ReadLocalAsync(
        FeedManifest manifest,
        string request,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.GetDirectoryName(manifest.SourcePath) ?? ".");
        var file = Path.GetFullPath(Path.Combine(root, request));

        if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{request}' is outside the adapter directory, so it was not read.");
        }

        if (!File.Exists(file))
        {
            throw new InvalidOperationException($"The feed file '{request}' does not exist.");
        }

        return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What to call a manifest when telling someone what happened.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>Its display name, or its key when it has none.</returns>
    private static string Name(FeedManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.Key : manifest.DisplayName;

    /// <summary>
    /// Finds the manifest that claims an address.
    /// </summary>
    /// <param name="manifests">The loaded manifests.</param>
    /// <param name="address">The address to match.</param>
    /// <returns>The manifest, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Highest priority wins, and file-name order breaks a tie. Both halves
    /// matter: the number is how someone says which of two overlapping feeds
    /// they meant, and the name is so that leaving the numbers alone still
    /// gives an answer they can predict and change by renaming a file.
    /// </remarks>
    private static FeedManifest? Find(IReadOnlyList<FeedManifest> manifests, Uri address) =>
        manifests
            .Where(manifest => Claims(manifest, address))
            .OrderByDescending(manifest => manifest.Priority)
            .ThenBy(manifest => manifest.SourcePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>Determines whether one manifest claims an address.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    private static bool Claims(FeedManifest manifest, Uri address)
    {
        var host = address.Host;

        var hostMatches = manifest.Match.Hosts.Any(candidate =>
            host.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase));

        if (!hostMatches)
        {
            return false;
        }

        return manifest.Match.PathContains.Count == 0 ||
               manifest.Match.PathContains.Any(fragment =>
                   address.AbsoluteUri.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fills a request template from the listing being installed.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="listing">The listing.</param>
    /// <param name="page">The address that matched.</param>
    /// <returns>The resolved address.</returns>
    /// <remarks>
    /// Values are escaped as they are substituted. A title containing an
    /// ampersand would otherwise end one query parameter and begin another,
    /// which is a query-injection bug however innocently it arrives.
    /// </remarks>
    private static string Substitute(string template, CatalogListing listing, Uri page)
    {
        var lastSegment = page.Segments.Length > 0
            ? Uri.UnescapeDataString(page.Segments[^1]).Trim('/')
            : string.Empty;

        return template
            .Replace("{url}", Uri.EscapeDataString(page.AbsoluteUri), StringComparison.Ordinal)
            .Replace("{host}", page.Host, StringComparison.Ordinal)
            .Replace("{path}", page.AbsolutePath, StringComparison.Ordinal)
            .Replace("{slug}", Uri.EscapeDataString(lastSegment), StringComparison.Ordinal)
            .Replace("{title}", Uri.EscapeDataString(listing.Title), StringComparison.Ordinal)
            .Replace("{id}", Uri.EscapeDataString(listing.ListingId), StringComparison.Ordinal)
            .Replace(
                "{year}",
                listing.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparison.Ordinal);
    }
}
