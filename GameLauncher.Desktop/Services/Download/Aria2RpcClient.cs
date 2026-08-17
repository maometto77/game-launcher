using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Desktop.Services.Download;

/// <summary>
/// What aria2 reports about a transfer in flight.
/// </summary>
/// <param name="CompletedBytes">Bytes fetched so far.</param>
/// <param name="TotalBytes">Total size, or <see langword="null"/> before it is known.</param>
/// <param name="BytesPerSecond">Current download rate.</param>
/// <param name="Connections">Peers or servers connected, or <see langword="null"/> when not reported.</param>
/// <param name="Seeders">
/// Seeders connected, or <see langword="null"/> for anything that is not a torrent.
/// </param>
public sealed record Aria2Status(
    long CompletedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    int? Connections,
    int? Seeders);

/// <summary>
/// The loopback endpoint and credential one aria2c process listens on.
/// </summary>
/// <remarks>
/// <para>
/// Made per transfer rather than once for the application. This launcher runs an
/// aria2c per download and lets it exit when the transfer ends, so there is no
/// daemon to keep alive, no port held while nothing is downloading, and nothing
/// to tear down when the window closes — the process is already bound to the
/// work it was started for.
/// </para>
/// <para>
/// The secret is 256 bits from the system generator, fresh each time, and the
/// listener is bound to loopback only. It is passed to aria2c on its command
/// line, which means another process running as this same user could read it
/// from the process list; it authorises control of one loopback endpoint that
/// exists for the length of one download. The alternative, a configuration file,
/// would make aria2c ignore the user's own <c>aria2.conf</c> — losing their proxy
/// and bandwidth settings to close a gap that only opens to someone already
/// running as them.
/// </para>
/// </remarks>
public sealed record Aria2RpcSession(int Port, string Secret)
{
    /// <summary>Gets the JSON-RPC endpoint aria2c serves.</summary>
    public Uri Endpoint => new($"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/jsonrpc");

    /// <summary>Gets the token form aria2 expects in every call's first parameter.</summary>
    public string Token => "token:" + Secret;

    /// <summary>
    /// Creates a session on a free loopback port with a fresh secret.
    /// </summary>
    /// <returns>The session.</returns>
    /// <remarks>
    /// The port is found by binding one and letting go. Something else could take
    /// it in the gap, in which case aria2c fails to bind and reports it — and the
    /// transport carries on without RPC rather than failing the download, because
    /// the transfer never depended on the statistics.
    /// </remarks>
    public static Aria2RpcSession Create()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            listener.Start();

            return new Aria2RpcSession(
                ((IPEndPoint)listener.LocalEndpoint).Port,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }
}

/// <summary>
/// Talks to a running aria2c over its JSON-RPC interface.
/// </summary>
/// <remarks>
/// <para>
/// Only two calls are needed: <c>aria2.tellActive</c>, for what is happening, and
/// <c>aria2.shutdown</c>, for stopping cleanly. Transfers are still started on
/// the command line, so nothing here can lose a download by failing.
/// </para>
/// <para>
/// aria2 encodes every number in this API as a string, including sizes and
/// speeds. Parsing is invariant and forgiving: a field this launcher cannot read
/// becomes <see langword="null"/> rather than an exception, because a statistic
/// is not worth interrupting a download over.
/// </para>
/// </remarks>
public sealed class Aria2RpcClient
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for RPC calls.</summary>
    public const string HttpClientName = "aria2-rpc";

    /// <summary>
    /// Fields asked for. Requesting only these keeps the response small and means
    /// an unrelated change to aria2's schema cannot break the parse.
    /// </summary>
    private static readonly string[] StatusKeys =
        ["gid", "completedLength", "totalLength", "downloadSpeed", "connections", "numSeeders"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly Aria2RpcSession _session;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="client">Client used for the calls.</param>
    /// <param name="session">Endpoint and credential to talk to.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Aria2RpcClient(HttpClient client, Aria2RpcSession session)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Asks what is downloading right now.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The transfer being reported, or <see langword="null"/> when nothing is
    /// active — which is also how a finished or not-yet-started download looks.
    /// </returns>
    /// <exception cref="HttpRequestException">aria2 could not be reached.</exception>
    /// <exception cref="JsonException">The response was not valid JSON.</exception>
    /// <remarks>
    /// When several transfers are active the largest is reported. Fetching a
    /// <c>.torrent</c> over HTTP and then fetching what it describes are two
    /// downloads to aria2, and the one worth showing a person is the payload
    /// rather than the few kilobytes of metadata that named it.
    /// </remarks>
    public async Task<Aria2Status?> TellActiveAsync(CancellationToken cancellationToken = default)
    {
        var response = await CallAsync("aria2.tellActive", [_session.Token, StatusKeys], cancellationToken)
            .ConfigureAwait(false);

        if (response?.Result is not { ValueKind: JsonValueKind.Array } results)
        {
            return null;
        }

        Aria2Status? best = null;

        foreach (var element in results.EnumerateArray())
        {
            var status = ReadStatus(element);

            if (best is null || (status.TotalBytes ?? 0) > (best.TotalBytes ?? 0))
            {
                best = status;
            }
        }

        return best;
    }

    /// <summary>
    /// Asks aria2 to stop.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the request has been sent.</returns>
    /// <remarks>
    /// Preferred over killing the process. A graceful shutdown lets aria2 write
    /// its control file, which is what makes the next attempt resume from where
    /// this one stopped instead of starting again. The caller still kills it if
    /// it does not go.
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        await CallAsync("aria2.shutdown", [_session.Token], cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Makes one JSON-RPC call.
    /// </summary>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Positional parameters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed envelope, or <see langword="null"/> when aria2 reported an error.</returns>
    private async Task<RpcResponse?> CallAsync(
        string method,
        object[] parameters,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            _session.Endpoint,
            new RpcRequest("2.0", "gl", method, parameters),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var envelope = await response.Content
            .ReadFromJsonAsync<RpcResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        // An RPC-level error — a wrong secret, an unknown method — is reported as
        // nothing rather than thrown. The transfer is running on the command line
        // and does not depend on this call succeeding.
        return envelope?.Error is null ? envelope : null;
    }

    /// <summary>
    /// Reads one status object.
    /// </summary>
    /// <param name="element">The object aria2 returned.</param>
    /// <returns>The status.</returns>
    private static Aria2Status ReadStatus(JsonElement element) => new(
        Number(element, "completedLength") ?? 0,

        // aria2 reports zero before it knows the size, which is not the same as
        // a zero-byte file and must not be shown as a complete progress bar.
        Number(element, "totalLength") is > 0 and var total ? total : null,
        Number(element, "downloadSpeed") ?? 0,
        Count(element, "connections"),

        // Absent for anything that is not a torrent, which is exactly the
        // distinction the interface wants: no seeder column for an HTTP fetch.
        Count(element, "numSeeders"));

    /// <summary>Reads a string-encoded integer field.</summary>
    /// <param name="element">The object to read from.</param>
    /// <param name="name">Field name.</param>
    /// <returns>The value, or <see langword="null"/> when absent or unreadable.</returns>
    private static long? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>Reads a string-encoded count, clamped to what an <see cref="int"/> holds.</summary>
    /// <param name="element">The object to read from.</param>
    /// <param name="name">Field name.</param>
    /// <returns>The count, or <see langword="null"/> when absent.</returns>
    private static int? Count(JsonElement element, string name) =>
        Number(element, name) is { } value ? (int)Math.Clamp(value, 0, int.MaxValue) : null;

    /// <summary>One JSON-RPC request.</summary>
    /// <param name="JsonRpc">Protocol version, always <c>2.0</c>.</param>
    /// <param name="Id">Correlation id; this client makes one call at a time.</param>
    /// <param name="Method">Method name.</param>
    /// <param name="Params">Positional parameters, the first being the token.</param>
    private sealed record RpcRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params);

    /// <summary>One JSON-RPC response.</summary>
    /// <param name="Result">The method's return value, when it succeeded.</param>
    /// <param name="Error">The failure, when it did not.</param>
    private sealed record RpcResponse(
        [property: JsonPropertyName("result")] JsonElement? Result,
        [property: JsonPropertyName("error")] RpcError? Error);

    /// <summary>A JSON-RPC error.</summary>
    /// <param name="Code">aria2's error code.</param>
    /// <param name="Message">Its description.</param>
    private sealed record RpcError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string? Message);
}
