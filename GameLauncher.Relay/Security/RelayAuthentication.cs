using System.Security.Claims;
using System.Text.Encodings.Web;
using GameLauncher.Relay.Data.Repositories;
using GameLauncher.Shared.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GameLauncher.Relay.Security;

/// <summary>
/// Claim types the relay issues.
/// </summary>
public static class RelayClaims
{
    /// <summary>The authenticated user's friend code.</summary>
    public const string FriendCode = "gl:friend_code";

    /// <summary>The device the presented token belongs to.</summary>
    public const string DeviceId = "gl:device_id";
}

/// <summary>
/// Convenience accessors for the relay's claims.
/// </summary>
public static class RelayPrincipalExtensions
{
    /// <summary>
    /// Gets the authenticated friend code.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The friend code.</returns>
    /// <exception cref="InvalidOperationException">
    /// The principal carries no friend code, which means an endpoint that
    /// requires authentication was reached without it.
    /// </exception>
    public static string GetFriendCode(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.FindFirstValue(RelayClaims.FriendCode)
               ?? throw new InvalidOperationException("The request is not authenticated.");
    }

    /// <summary>Gets the authenticated device identifier, if present.</summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The device identifier, or <see langword="null"/>.</returns>
    public static string? GetDeviceId(this ClaimsPrincipal principal) =>
        principal?.FindFirstValue(RelayClaims.DeviceId);
}

/// <summary>
/// Authenticates bearer tokens issued at registration.
/// </summary>
/// <remarks>
/// A database-backed scheme rather than a JWT. A JWT would validate without a
/// read, at the cost of not being revocable before it expires — and on a small
/// self-hosted service, "get that machine off my account" should take effect at
/// once. The cost is one indexed lookup against a table holding as many rows as
/// the user has devices.
/// </remarks>
public sealed class RelayAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Name of the authentication scheme.</summary>
    public const string SchemeName = "RelayToken";

    private readonly ITokenService _tokens;
    private readonly IDeviceRepository _devices;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="options">Scheme options.</param>
    /// <param name="logger">Logger factory supplied by the framework.</param>
    /// <param name="encoder">URL encoder supplied by the framework.</param>
    /// <param name="tokens">Hashes the presented token.</param>
    /// <param name="devices">Looks the device up by token hash.</param>
    public RelayAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ITokenService tokens,
        IDeviceRepository devices)
        : base(options, logger, encoder)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken();

        if (string.IsNullOrWhiteSpace(token))
        {
            // NoResult rather than Fail: the request simply did not attempt to
            // authenticate, which is not an error on an anonymous endpoint.
            return AuthenticateResult.NoResult();
        }

        var device = await _devices
            .FindByTokenHashAsync(_tokens.Hash(token), Context.RequestAborted)
            .ConfigureAwait(false);

        if (device is null)
        {
            // One message for "unknown token" and "revoked token" alike: telling
            // them apart would confirm to a caller that a token was once valid.
            return AuthenticateResult.Fail("The supplied token is not valid.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(RelayClaims.FriendCode, device.FriendCode),
                new Claim(RelayClaims.DeviceId, device.DeviceId),
                new Claim(ClaimTypes.NameIdentifier, device.FriendCode)
            ],
            SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>
    /// Reads the token from the request.
    /// </summary>
    /// <returns>The token, or <see langword="null"/> when none was supplied.</returns>
    /// <remarks>
    /// The <c>Authorization</c> header is preferred. The query string is accepted
    /// only for the hub path, because a WebSocket handshake cannot carry custom
    /// headers — restricting it to that path keeps tokens out of access logs for
    /// ordinary requests, where they would otherwise be recorded in the clear.
    /// </remarks>
    private string? ExtractToken()
    {
        var header = Request.Headers.Authorization.ToString();

        if (!string.IsNullOrWhiteSpace(header) &&
            header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        if (Request.Path.StartsWithSegments(PresenceHubContract.Path) &&
            Request.Query.TryGetValue(PresenceHubContract.AccessTokenQueryParameter, out var fromQuery))
        {
            return fromQuery.ToString();
        }

        return null;
    }
}
