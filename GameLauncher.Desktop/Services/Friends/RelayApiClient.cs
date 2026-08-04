using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// Default <see cref="IRelayApiClient"/>.
/// </summary>
/// <remarks>
/// The base address and token are read from settings on every call rather than
/// captured once. That is what makes changing the relay address, or registering
/// for the first time, take effect immediately instead of after a restart — and
/// it is the whole of what "token refresh" needs to mean here, since tokens do
/// not expire.
/// </remarks>
public sealed class RelayApiClient : IRelayApiClient
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used for relay calls.</summary>
    public const string HttpClientName = "relay";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<RelayApiClient> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the configured client.</param>
    /// <param name="settings">Supplies the relay address and token.</param>
    /// <param name="logger">Logger for relay diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RelayApiClient(
        IHttpClientFactory httpClientFactory,
        ISettingsService settings,
        ILogger<RelayApiClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsConfigured => _settings.Current.HasRelay;

    /// <inheritdoc />
    public Task<RelayInfo> GetRelayInfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync<RelayInfo>(
            HttpMethod.Get, "relay-info", body: null, requiresToken: false, cancellationToken);

    /// <inheritdoc />
    public Task<RegisterResponse> RegisterAsync(
        string displayName,
        CancellationToken cancellationToken = default) =>
        SendAsync<RegisterResponse>(
            HttpMethod.Post,
            "register",
            new RegisterRequest { DisplayName = displayName },
            requiresToken: false,
            cancellationToken);

    /// <inheritdoc />
    public Task<FriendListResponse> GetFriendsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<FriendListResponse>(
            HttpMethod.Get, "friends", body: null, requiresToken: true, cancellationToken);

    /// <inheritdoc />
    public Task<CatalogResolveResponse> ResolveCatalogAsync(
        CatalogResolveRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<CatalogResolveResponse>(
            HttpMethod.Post, "catalog/resolve", request, requiresToken: true, cancellationToken);

    /// <inheritdoc />
    public Task<AchievementSyncResponse> SyncAchievementsAsync(
        AchievementSyncRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AchievementSyncResponse>(
            HttpMethod.Post, "sync/achievements", request, requiresToken: true, cancellationToken);

    /// <summary>
    /// Issues a request and deserialises the response.
    /// </summary>
    /// <typeparam name="TResponse">Expected response shape.</typeparam>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path relative to the configured relay address.</param>
    /// <param name="body">Request body, or <see langword="null"/>.</param>
    /// <param name="requiresToken">Whether the call must be authenticated.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The deserialised response.</returns>
    /// <exception cref="RelayApiException">The call failed for any reason.</exception>
    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        object? body,
        bool requiresToken,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        if (!settings.HasRelay)
        {
            throw new RelayApiException("No relay address is configured.", isTransient: false);
        }

        if (!Uri.TryCreate(settings.RelayUrl, UriKind.Absolute, out var baseAddress))
        {
            throw new RelayApiException(
                $"The relay address '{settings.RelayUrl}' is not a valid URL.", isTransient: false);
        }

        if (requiresToken && string.IsNullOrWhiteSpace(settings.ActiveAuthToken))
        {
            // Not transient: retrying without a token will fail identically. The
            // coordinator registers first, then retries.
            throw new RelayApiException("This installation has not registered with the relay.", isTransient: false);
        }

        using var request = new HttpRequestMessage(method, new Uri(baseAddress, path));

        if (requiresToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ActiveAuthToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: SerializerOptions);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            // The relay being unreachable is the expected state for an
            // offline-first launcher, not an error worth an error-level log.
            _logger.LogDebug(ex, "Relay call {Method} {Path} could not reach the server.", method, path);
            throw new RelayApiException("The relay could not be reached.", isTransient: true, ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content
                    .ReadFromJsonAsync<TResponse>(SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                return payload ?? throw new RelayApiException(
                    "The relay returned an empty response.", isTransient: true);
            }

            throw await BuildFailureAsync(response, method, path, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Turns a failed response into an exception the caller can act on.</summary>
    /// <param name="response">The failed response.</param>
    /// <param name="method">HTTP method, for logging.</param>
    /// <param name="path">Request path, for logging.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The exception to throw.</returns>
    private async Task<RelayApiException> BuildFailureAsync(
        HttpResponseMessage response,
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        string? detail = null;

        try
        {
            var error = await response.Content
                .ReadFromJsonAsync<ErrorResponse>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            detail = error?.Detail ?? error?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Not every failure carries the relay's error shape — a reverse proxy
            // returning 502 will not — so the status code has to stand alone.
        }

        // 5xx and 408 are worth another attempt; 4xx means the request itself is
        // wrong and will fail identically forever.
        var isTransient =
            (int)response.StatusCode >= 500 ||
            response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

        _logger.LogWarning(
            "Relay call {Method} {Path} failed with {Status}: {Detail}",
            method, path, (int)response.StatusCode, detail ?? "no detail");

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "The relay rejected this installation's token. Re-register in Settings.",
            HttpStatusCode.NotFound =>
                "The relay address is reachable but does not look like a GameLauncher relay.",
            _ => detail ?? $"The relay returned {(int)response.StatusCode}."
        };

        return new RelayApiException(message, isTransient);
    }
}
