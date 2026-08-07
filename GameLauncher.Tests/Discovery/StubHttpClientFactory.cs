using System.Net;
using System.Net.Http;

namespace GameLauncher.Tests.Discovery;

/// <summary>
/// Serves canned responses matched by substring against the request address.
/// </summary>
/// <remarks>
/// Used where the interesting behaviour is what a source does with a payload
/// rather than how bytes move — the download tests already cover real sockets
/// through <c>LoopbackFileServer</c>. This keeps a source's mapping testable
/// against a captured payload with no server to start.
/// </remarks>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly List<(string Fragment, Func<HttpResponseMessage> Respond)> _routes = [];

    /// <summary>Addresses that were requested, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Adds a route returning a JSON body.</summary>
    /// <param name="fragment">Substring the request address must contain.</param>
    /// <param name="json">The body to return.</param>
    /// <returns>This instance, for chaining.</returns>
    public StubHttpClientFactory Json(string fragment, string json)
    {
        _routes.Add((fragment, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }));

        return this;
    }

    /// <summary>Adds a route returning a status with no body.</summary>
    /// <param name="fragment">Substring the request address must contain.</param>
    /// <param name="status">The status to return.</param>
    /// <returns>This instance, for chaining.</returns>
    public StubHttpClientFactory Status(string fragment, HttpStatusCode status)
    {
        _routes.Add((fragment, () => new HttpResponseMessage(status)));
        return this;
    }

    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new(new StubHandler(this))
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    /// <summary>Finds the route for an address.</summary>
    /// <param name="url">The requested address.</param>
    /// <returns>The response, or a 404 when nothing matched.</returns>
    private HttpResponseMessage Respond(string url)
    {
        Requests.Add(url);

        foreach (var (fragment, respond) in _routes)
        {
            if (url.Contains(fragment, StringComparison.Ordinal))
            {
                return respond();
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Routes every request through the owning factory.</summary>
    private sealed class StubHandler(StubHttpClientFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(owner.Respond(request.RequestUri!.ToString()));
    }
}
