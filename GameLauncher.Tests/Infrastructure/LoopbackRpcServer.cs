using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// One JSON-RPC call the server received.
/// </summary>
/// <param name="Method">Method name.</param>
/// <param name="Token">The first parameter, which aria2 expects to be the token.</param>
public sealed record RecordedRpcCall(string Method, string? Token);

/// <summary>
/// Stands in for a running <c>aria2c</c>'s JSON-RPC interface.
/// </summary>
/// <remarks>
/// A real socket serving real response bodies, rather than a stubbed client.
/// Everything interesting about this integration is in the wire format — aria2
/// encodes every number as a string, omits fields that do not apply, and reports
/// several active downloads while one <c>.torrent</c> fetches the payload it
/// describes — and none of that is exercised by a fake that returns objects.
/// </remarks>
public sealed class LoopbackRpcServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<RecordedRpcCall> _calls = [];

    private LoopbackRpcServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Gets the address the server is listening on.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Gets the port it is listening on.</summary>
    public int Port => BaseAddress.Port;

    /// <summary>Gets or sets the raw body returned to every call.</summary>
    /// <remarks>
    /// Raw text rather than a serialised object, so a test can hand over exactly
    /// what aria2 sends — including a malformed body, which is a thing that has
    /// to be survivable.
    /// </remarks>
    public string ResponseBody { get; set; } = """{"id":"gl","jsonrpc":"2.0","result":[]}""";

    /// <summary>Gets or sets the status returned to every call.</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Gets every call received, in order.</summary>
    public IReadOnlyList<RecordedRpcCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts a server on a loopback port.
    /// </summary>
    /// <param name="port">The port to bind, or zero for any free one.</param>
    /// <returns>The running server.</returns>
    /// <remarks>
    /// A specific port is what lets a test stand in for the aria2c that the
    /// transport has already launched: the transport chooses the port, tells its
    /// process about it on the command line, and a test that reads that command
    /// line can bind it and answer.
    /// </remarks>
    public static async Task<LoopbackRpcServer> StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddRoutingCore();

        var app = builder.Build();
        var holder = new Holder();

        app.MapPost("/jsonrpc", async (HttpContext context) =>
        {
            var server = holder.Instance!;

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            server.Record(body);

            context.Response.StatusCode = server.StatusCode;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync(server.ResponseBody);
        });

        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        var instance = new LoopbackRpcServer(app, new Uri(address));
        holder.Instance = instance;

        return instance;
    }

    /// <summary>Stops the server.</summary>
    /// <returns>A task that completes once it has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Notes what was asked.</summary>
    /// <param name="body">The request body.</param>
    private void Record(string body)
    {
        string method;
        string? token = null;

        try
        {
            using var document = JsonDocument.Parse(body);

            method = document.RootElement.GetProperty("method").GetString() ?? string.Empty;

            if (document.RootElement.TryGetProperty("params", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Array &&
                parameters.GetArrayLength() > 0 &&
                parameters[0].ValueKind == JsonValueKind.String)
            {
                token = parameters[0].GetString();
            }
        }
        catch (JsonException)
        {
            method = "<unparseable>";
        }

        lock (_calls)
        {
            _calls.Add(new RecordedRpcCall(method, token));
        }
    }

    /// <summary>Lets the routes reach the instance built after them.</summary>
    private sealed class Holder
    {
        public LoopbackRpcServer? Instance { get; set; }
    }
}
