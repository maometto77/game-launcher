using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Discovery.Http;
using GameLauncher.Desktop.Services.Discovery.Sources;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing;

/// <summary>
/// Sourcing adapter for MyAbandonware.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It refuses, and that is its whole job.</strong> The site's
/// <c>robots.txt</c> disallows <c>/download/*</c> for every crawler, so there is
/// no download this adapter can honestly produce. It exists so the refusal is
/// explicit, checked against the live rules rather than assumed, and routed to
/// the fallback that finds the same game somewhere it can be fetched from.
/// </para>
/// <para>
/// Writing it as a real adapter rather than leaving a gap matters: the decision
/// is stated once, in one place, with a test that fails if the behaviour ever
/// changes. A missing adapter would be indistinguishable from an oversight.
/// </para>
/// </remarks>
public sealed class MyAbandonwareSourcingAdapter : ISourcingAdapter
{
    private const string SiteHost = "myabandonware.com";

    private readonly IRobotsPolicy _robots;
    private readonly ILogger<MyAbandonwareSourcingAdapter> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="robots">Checks the site's published rules.</param>
    /// <param name="logger">Logger for sourcing diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MyAbandonwareSourcingAdapter(IRobotsPolicy robots, ILogger<MyAbandonwareSourcingAdapter> logger)
    {
        _robots = robots ?? throw new ArgumentNullException(nameof(robots));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Key => MyAbandonwareCatalogSource.SourceKey;

    /// <inheritdoc />
    public string DisplayName => "MyAbandonware";

    /// <inheritdoc />
    public bool CanHandle(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        parsed.Host.EndsWith(SiteHost, StringComparison.OrdinalIgnoreCase);

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

        // Asked of the live rules rather than hardcoded. If the site ever opens
        // the path, this starts answering differently without a code change —
        // and until it does, the refusal is a fact rather than an assumption.
        var downloadPath = new Uri(page, "/download/");

        if (await _robots.IsAllowedAsync(downloadPath, cancellationToken).ConfigureAwait(false))
        {
            // Deliberately not implemented rather than left as a silent gap:
            // the rules permit it, but nothing here has been written or tested
            // against a real page, and guessing at one would be worse than
            // saying so.
            _logger.LogInformation(
                "{Site} now permits {Path}, but no download extraction is implemented for it.",
                DisplayName, downloadPath);

            return new SourcingPayload(
                [],
                SourcingRefusal.NoPayload,
                $"{DisplayName} permits downloads but this launcher does not read them yet.");
        }

        _logger.LogDebug(
            "{Site} disallows {Path} in robots.txt; looking for another source.", DisplayName, downloadPath);

        return new SourcingPayload(
            [],
            SourcingRefusal.DisallowedByRobots,
            $"{DisplayName} does not permit automated downloads, so '{listing.Title}' " +
            "is described here but fetched from elsewhere.");
    }
}
