using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// One request the server handled, as the download service actually sent it.
/// </summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Request path.</param>
/// <param name="Range">The <c>Range</c> header, or <see langword="null"/> when absent.</param>
public sealed record RecordedRequest(string Method, string Path, string? Range);

/// <summary>
/// A real HTTP server on the loopback interface, serving files the tests
/// control.
/// </summary>
/// <remarks>
/// <para>
/// Kestrel rather than a stubbed <see cref="System.Net.Http.HttpMessageHandler"/>,
/// so the tests exercise the networking stack the application actually uses:
/// sockets, chunked transfer, redirect following, range negotiation and mid-body
/// connection loss. A handler stub would prove the download service calls
/// <c>SendAsync</c> correctly and nothing about whether the transfer works.
/// </para>
/// <para>
/// Kestrel rather than <see cref="HttpListener"/> because Kestrel binds an
/// ordinary socket. <see cref="HttpListener"/> needs a URL ACL reservation on
/// Windows, which a test run cannot assume it has.
/// </para>
/// <para>
/// Port zero: the operating system picks a free port, so a run never collides
/// with another process or with a parallel test collection.
/// </para>
/// </remarks>
public sealed class LoopbackFileServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    private LoopbackFileServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Gets the address the server is listening on.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Gets every request the server has handled, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>
    /// Gets or sets how many bytes to send before dropping the connection, or
    /// <see langword="null"/> to serve the whole body.
    /// </summary>
    /// <remarks>
    /// Applies to the <c>/unstable/</c> route only, so one server can serve both a
    /// failing transfer and the resume that follows it.
    /// </remarks>
    public int? DropConnectionAfterBytes { get; set; }

    /// <summary>
    /// Gets or sets a delay applied between body chunks, used to keep a transfer
    /// running long enough to cancel it.
    /// </summary>
    public TimeSpan ChunkDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Gets or sets the size of each body chunk written.</summary>
    public int ChunkSize { get; set; } = 16 * 1024;

    /// <summary>
    /// Starts a server on a free loopback port.
    /// </summary>
    /// <returns>The running server.</returns>
    public static async Task<LoopbackFileServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRoutingCore();

        var app = builder.Build();
        var server = new LoopbackFileServerBuilder();

        // Straightforward file, with range support. The workhorse route.
        app.MapMethods("/files/{name}", [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, string name) => server.Instance!.ServeAsync(context, name, allowRange: true));

        // Advertises ranges and then ignores them, which real servers do. The
        // download service must notice from the 200 and restart rather than
        // appending a second copy of the file to the partial one.
        app.MapMethods("/no-range/{name}", [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, string name) => server.Instance!.ServeAsync(context, name, allowRange: false));

        // Drops the connection partway through, to produce a genuinely interrupted
        // transfer rather than a simulated one.
        app.MapMethods("/unstable/{name}", [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, string name) => server.Instance!.ServeAsync(context, name, allowRange: true, unstable: true));

        // A redirect chain ending at the real file.
        app.MapMethods("/redirect/{hops:int}/{name}", [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, int hops, string name) =>
            {
                server.Instance!.Record(context);

                var next = hops <= 1 ? $"/files/{name}" : $"/redirect/{hops - 1}/{name}";
                context.Response.Redirect(next, permanent: false);

                return Task.CompletedTask;
            });

        // Suggests a file name through Content-Disposition, including a hostile one.
        app.MapMethods("/named/{name}", [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, string name) =>
            {
                var suggested = context.Request.Query["as"].ToString();

                if (!string.IsNullOrEmpty(suggested))
                {
                    context.Response.Headers.ContentDisposition = $"attachment; filename=\"{suggested}\"";
                }

                return server.Instance!.ServeAsync(context, name, allowRange: true);
            });

        app.MapGet("/missing/{name}", (HttpContext context, string name) =>
        {
            server.Instance!.Record(context);
            return Results.NotFound();
        });

        await app.StartAsync().ConfigureAwait(false);

        var address = app.Urls.First();
        var instance = new LoopbackFileServer(app, new Uri(address));

        server.Instance = instance;
        return instance;
    }

    /// <summary>
    /// Publishes content at a name, replacing anything already there.
    /// </summary>
    /// <param name="name">File name the routes address it by.</param>
    /// <param name="content">Bytes to serve.</param>
    public void AddFile(string name, byte[] content) => _files[name] = content;

    /// <summary>Builds the URL of a file on the ordinary route.</summary>
    /// <param name="name">File name.</param>
    /// <returns>An absolute URL.</returns>
    public Uri FileUrl(string name) => new(BaseAddress, $"/files/{name}");

    /// <summary>Builds the URL of a file on a route that ignores range requests.</summary>
    /// <param name="name">File name.</param>
    /// <returns>An absolute URL.</returns>
    public Uri NoRangeUrl(string name) => new(BaseAddress, $"/no-range/{name}");

    /// <summary>Builds the URL of a file on the route that drops the connection.</summary>
    /// <param name="name">File name.</param>
    /// <returns>An absolute URL.</returns>
    public Uri UnstableUrl(string name) => new(BaseAddress, $"/unstable/{name}");

    /// <summary>Builds a URL that redirects the given number of times before the file.</summary>
    /// <param name="hops">How many redirects to traverse.</param>
    /// <param name="name">File name.</param>
    /// <returns>An absolute URL.</returns>
    public Uri RedirectUrl(int hops, string name) => new(BaseAddress, $"/redirect/{hops}/{name}");

    /// <summary>Builds a URL that suggests a file name through Content-Disposition.</summary>
    /// <param name="name">File name.</param>
    /// <param name="suggested">The name to suggest.</param>
    /// <returns>An absolute URL.</returns>
    public Uri NamedUrl(string name, string suggested) =>
        new(BaseAddress, $"/named/{name}?as={Uri.EscapeDataString(suggested)}");

    /// <summary>Forgets every recorded request.</summary>
    public void ClearRequests() => _requests.Clear();

    /// <summary>
    /// Serves a file, honouring a range request when the route allows it.
    /// </summary>
    /// <param name="context">The request being served.</param>
    /// <param name="name">Which published file to serve.</param>
    /// <param name="allowRange">Whether to act on a <c>Range</c> header.</param>
    /// <param name="unstable">Whether to drop the connection partway through.</param>
    private async Task ServeAsync(HttpContext context, string name, bool allowRange, bool unstable = false)
    {
        Record(context);

        if (!_files.TryGetValue(name, out var content))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var offset = 0;
        var partial = false;

        if (allowRange && TryParseRangeStart(context.Request.Headers.Range.ToString(), out var start)
                       && start < content.Length)
        {
            offset = (int)start;
            partial = true;
        }

        // Always advertised, including on the route that then ignores it — that
        // combination is exactly what makes trusting the header unsafe.
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.ContentType = "application/octet-stream";

        if (partial)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = $"bytes {offset}-{content.Length - 1}/{content.Length}";
        }

        context.Response.ContentLength = content.Length - offset;

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        var limit = unstable && DropConnectionAfterBytes is { } cap
            ? Math.Min(cap, content.Length - offset)
            : content.Length - offset;

        var written = 0;

        while (written < limit)
        {
            var size = Math.Min(ChunkSize, limit - written);

            await context.Response.Body
                .WriteAsync(content.AsMemory(offset + written, size), context.RequestAborted)
                .ConfigureAwait(false);

            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

            written += size;

            if (ChunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(ChunkDelay, context.RequestAborted).ConfigureAwait(false);
            }
        }

        if (unstable && DropConnectionAfterBytes is not null)
        {
            // A real reset rather than a tidy close. The declared Content-Length
            // has not been satisfied, so the client sees the transfer fail
            // mid-body — which is the situation resume exists for.
            context.Abort();
        }
    }

    /// <summary>Records a request for later assertion.</summary>
    /// <param name="context">The request being handled.</param>
    private void Record(HttpContext context)
    {
        var range = context.Request.Headers.Range.ToString();

        _requests.Enqueue(new RecordedRequest(
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            string.IsNullOrEmpty(range) ? null : range));
    }

    /// <summary>
    /// Reads the first byte position out of a <c>Range</c> header.
    /// </summary>
    /// <param name="header">The header value.</param>
    /// <param name="start">Receives the first byte position.</param>
    /// <returns><see langword="true"/> when the header is an open-ended byte range.</returns>
    /// <remarks>
    /// Only <c>bytes=N-</c> is understood, which is the only form the download
    /// service sends. Anything else is served as a complete response.
    /// </remarks>
    private static bool TryParseRangeStart(string? header, out long start)
    {
        start = 0;

        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header["bytes=".Length..];
        var dash = spec.IndexOf('-');

        return dash > 0 && long.TryParse(spec[..dash], out start) && start >= 0;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Holds the server instance while its routes are being declared.
    /// </summary>
    /// <remarks>
    /// The routes have to close over the instance, but the instance cannot be
    /// constructed until the application it wraps has been built. This box breaks
    /// that circle without making the field mutable on the server itself.
    /// </remarks>
    private sealed class LoopbackFileServerBuilder
    {
        public LoopbackFileServer? Instance { get; set; }
    }
}
