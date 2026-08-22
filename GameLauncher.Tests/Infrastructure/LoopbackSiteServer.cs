using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// A small web site on the loopback interface, for crawler tests.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="LoopbackFileServer"/>, which serves file bytes for
/// download tests. This one serves HTML at whatever paths a test asks for, which
/// is what a crawler needs: relative links, pagination, a robots.txt, and the
/// awkward answers a real site gives — a 404, a redirect, a body far larger than
/// it claimed.
/// </para>
/// <para>
/// No test in this suite talks to a real web site. Everything a crawler must
/// cope with is reproduced here deliberately, because a real site cannot be
/// asked to return a malformed page on demand.
/// </para>
/// </remarks>
public sealed class LoopbackSiteServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, PageEntry> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _requests = new();

    private int _failuresServed;

    private LoopbackSiteServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Gets the address the site is listening on.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Gets the paths that have been requested, in order.</summary>
    public IReadOnlyList<string> Requests => _requests.ToArray();

    /// <summary>
    /// Gets or sets the robots.txt body, or <see langword="null"/> for a 404.
    /// </summary>
    /// <remarks>
    /// Null by default, which a robots policy reads as "nothing disallowed" —
    /// the same as most sites. Set it to exercise a denial.
    /// </remarks>
    public string? Robots { get; set; }

    /// <summary>Gets or sets how many requests to fail before answering normally.</summary>
    /// <remarks>
    /// For the retry path: the first calls answer 503 and the one after
    /// succeeds, which is what a briefly unhappy site looks like. Requests for
    /// robots.txt are exempt, so a test can exercise retries without also
    /// breaking the politeness check that precedes them.
    /// </remarks>
    public int FailFirstRequests { get; set; }

    /// <summary>Starts a site on a free loopback port.</summary>
    /// <returns>The running site.</returns>
    public static async Task<LoopbackSiteServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRoutingCore();

        var app = builder.Build();
        var holder = new Holder();

        app.Run(context => holder.Instance!.HandleAsync(context));

        await app.StartAsync().ConfigureAwait(false);

        var instance = new LoopbackSiteServer(app, new Uri(app.Urls.First()));

        holder.Instance = instance;

        return instance;
    }

    /// <summary>Serves an HTML page at a path.</summary>
    /// <param name="path">The path, with or without a leading slash.</param>
    /// <param name="html">The body.</param>
    /// <returns>This server, for chaining.</returns>
    public LoopbackSiteServer AddPage(string path, string html)
    {
        _pages[Normalize(path)] = new PageEntry(html, "text/html; charset=utf-8", HttpStatusCode.OK, null);

        return this;
    }

    /// <summary>Serves a body with an explicit content type.</summary>
    /// <param name="path">The path.</param>
    /// <param name="body">The body.</param>
    /// <param name="contentType">The media type to declare.</param>
    /// <returns>This server, for chaining.</returns>
    public LoopbackSiteServer AddContent(string path, string body, string contentType)
    {
        _pages[Normalize(path)] = new PageEntry(body, contentType, HttpStatusCode.OK, null);

        return this;
    }

    /// <summary>Answers a path with a status and no useful body.</summary>
    /// <param name="path">The path.</param>
    /// <param name="status">The status to return.</param>
    /// <returns>This server, for chaining.</returns>
    public LoopbackSiteServer AddStatus(string path, HttpStatusCode status)
    {
        _pages[Normalize(path)] = new PageEntry(string.Empty, "text/plain", status, null);

        return this;
    }

    /// <summary>Redirects a path somewhere else.</summary>
    /// <param name="path">The path.</param>
    /// <param name="target">Where to send the caller.</param>
    /// <returns>This server, for chaining.</returns>
    public LoopbackSiteServer AddRedirect(string path, string target)
    {
        _pages[Normalize(path)] = new PageEntry(string.Empty, "text/plain", HttpStatusCode.Found, target);

        return this;
    }

    /// <summary>Builds an absolute address for a path on this site.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The address.</returns>
    public Uri Url(string path) => new(BaseAddress, path);

    /// <summary>Forgets the recorded requests.</summary>
    public void ClearRequests() => _requests.Clear();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Answers one request.</summary>
    /// <param name="context">The request being served.</param>
    /// <returns>A task that completes when the response is written.</returns>
    private async Task HandleAsync(HttpContext context)
    {
        var path = Normalize(context.Request.Path + context.Request.QueryString);

        _requests.Enqueue(path);

        if (path.StartsWith("/robots.txt", StringComparison.OrdinalIgnoreCase))
        {
            if (Robots is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(Robots).ConfigureAwait(false);

            return;
        }

        if (_failuresServed < FailFirstRequests)
        {
            _failuresServed++;
            context.Response.StatusCode = 503;

            return;
        }

        if (!_pages.TryGetValue(path, out var entry))
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("not found").ConfigureAwait(false);

            return;
        }

        if (entry.RedirectTo is { } target)
        {
            context.Response.StatusCode = (int)entry.Status;
            context.Response.Headers.Location = target;

            return;
        }

        context.Response.StatusCode = (int)entry.Status;
        context.Response.ContentType = entry.ContentType;

        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(entry.Body)).ConfigureAwait(false);
    }

    /// <summary>Puts a path into the form the table is keyed by.</summary>
    /// <param name="path">The path as given.</param>
    /// <returns>The normalised path.</returns>
    private static string Normalize(string path)
    {
        var trimmed = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    /// <summary>Lets the request handler reach the instance it belongs to.</summary>
    /// <remarks>
    /// The pipeline has to be described before the application starts, and the
    /// instance cannot exist until it has, because its address is only known
    /// afterwards.
    /// </remarks>
    private sealed class Holder
    {
        public LoopbackSiteServer? Instance { get; set; }
    }

    /// <summary>One canned response.</summary>
    /// <param name="Body">What to write.</param>
    /// <param name="ContentType">The media type to declare.</param>
    /// <param name="Status">The status to return.</param>
    /// <param name="RedirectTo">Where to redirect, when this is a redirect.</param>
    private sealed record PageEntry(
        string Body,
        string ContentType,
        HttpStatusCode Status,
        string? RedirectTo);
}
