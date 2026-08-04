using System.Security.Claims;
using GameLauncher.Relay.Data.Repositories;
using GameLauncher.Relay.Security;
using GameLauncher.Shared.Contracts;
using GameLauncher.Shared.Enums;
using Microsoft.AspNetCore.Mvc;

namespace GameLauncher.Relay.Endpoints;

/// <summary>
/// The relay's HTTP surface.
/// </summary>
/// <remarks>
/// Minimal APIs rather than controllers: the surface is small, every endpoint is
/// a single operation, and grouping them by concern in one file keeps the whole
/// contract readable at once.
/// </remarks>
public static class RelayEndpoints
{
    /// <summary>Prefix on every server-assigned catalog identity.</summary>
    /// <remarks>
    /// Distinguishes an assigned id from a client's provisional <c>local:</c> one
    /// at a glance, in a log or a database browser.
    /// </remarks>
    public const string CatalogIdPrefix = "app_";

    /// <summary>
    /// Maps every relay endpoint.
    /// </summary>
    /// <param name="app">The application to map onto.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapRelayEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapHealth(app);
        MapRelayInfo(app);
        MapRegistration(app);
        MapFriends(app);
        MapCatalog(app);
        MapSync(app);

        return app;
    }

    /// <summary>Maps the liveness probe.</summary>
    /// <param name="app">The application to map onto.</param>
    private static void MapHealth(WebApplication app) =>
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
           .AllowAnonymous()
           .WithName("Health");

    /// <summary>
    /// Maps the relay's self-description.
    /// </summary>
    /// <param name="app">The application to map onto.</param>
    /// <remarks>
    /// Anonymous, because a client must be able to learn which relay it is
    /// talking to <em>before</em> deciding which stored credentials to present —
    /// or whether it has any for this relay at all.
    /// </remarks>
    private static void MapRelayInfo(WebApplication app) =>
        app.MapGet("/relay-info", async (
                Data.RelayDatabaseInitializer initializer,
                CancellationToken cancellationToken) =>
            {
                var relayId = await initializer
                    .GetOrCreateRelayIdAsync(cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new RelayInfo
                {
                    RelayId = relayId,
                    Name = "GameLauncher relay",
                    SchemaVersion = Data.RelayDatabaseInitializer.TargetVersion
                });
            })
            .AllowAnonymous()
            .WithName("RelayInfo");

    /// <summary>Maps registration.</summary>
    /// <param name="app">The application to map onto.</param>
    private static void MapRegistration(WebApplication app) =>
        app.MapPost("/register", async (
                RegisterRequest request,
                IUserRepository users,
                IDeviceRepository devices,
                ITokenService tokens,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var logger = loggerFactory.CreateLogger("Registration");

                var displayName = request.DisplayName?.Trim() ?? string.Empty;

                if (displayName.Length is 0 or > 64)
                {
                    return Results.BadRequest(new ErrorResponse
                    {
                        Error = "invalid_display_name",
                        Detail = "A display name of 1 to 64 characters is required."
                    });
                }

                // Retried rather than assumed unique. Fifty bits of entropy makes a
                // collision vanishingly unlikely, but "vanishingly unlikely" is not
                // "impossible", and the consequence would be handing a new user
                // somebody else's identity.
                string friendCode;
                var attempt = 0;

                do
                {
                    friendCode = tokens.NewFriendCode();
                    attempt++;
                }
                while (await users.GetAsync(friendCode, cancellationToken).ConfigureAwait(false) is not null
                       && attempt < 5);

                var now = DateTimeOffset.UtcNow;

                await users.AddAsync(new RelayUser
                {
                    FriendCode = friendCode,
                    DisplayName = displayName,
                    CreatedAt = now,
                    UpdatedAt = now
                }, cancellationToken).ConfigureAwait(false);

                var token = tokens.NewToken();
                var deviceId = tokens.NewDeviceId();

                await devices.AddAsync(new RelayDevice
                {
                    DeviceId = deviceId,
                    FriendCode = friendCode,

                    // Only the hash is stored. The token below is the only time it
                    // is ever visible, and it cannot be recovered or reissued.
                    TokenHash = tokens.Hash(token),
                    Label = "First device",
                    CreatedAt = now,
                    LastSeenAt = now
                }, cancellationToken).ConfigureAwait(false);

                logger.LogInformation("Registered {FriendCode} with device {DeviceId}.", friendCode, deviceId);

                return Results.Ok(new RegisterResponse
                {
                    FriendCode = friendCode,
                    AuthToken = token,
                    DeviceId = deviceId
                });
            })
            .AllowAnonymous()
            .WithName("Register");

    /// <summary>Maps the friend list.</summary>
    /// <param name="app">The application to map onto.</param>
    private static void MapFriends(WebApplication app) =>
        app.MapGet("/friends", async (
                ClaimsPrincipal principal,
                IUserRepository users,
                IFriendshipRepository friendships,
                IPresenceRepository presences,
                CancellationToken cancellationToken) =>
            {
                var caller = principal.GetFriendCode();

                var relationships = await friendships
                    .GetForUserAsync(caller, cancellationToken)
                    .ConfigureAwait(false);

                if (relationships.Count == 0)
                {
                    return Results.Ok(new FriendListResponse());
                }

                var otherCodes = relationships
                    .Select(row => string.Equals(row.UserFriendCode, caller, StringComparison.Ordinal)
                        ? row.FriendFriendCode
                        : row.UserFriendCode)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                // Two batched reads rather than one per friend.
                var profiles = await users.GetManyAsync(otherCodes, cancellationToken).ConfigureAwait(false);
                var presence = await presences.GetManyAsync(otherCodes, cancellationToken).ConfigureAwait(false);

                var friends = new List<FriendDto>(relationships.Count);

                foreach (var relationship in relationships)
                {
                    var isOutgoing = string.Equals(relationship.UserFriendCode, caller, StringComparison.Ordinal);
                    var other = isOutgoing ? relationship.FriendFriendCode : relationship.UserFriendCode;

                    if (!profiles.TryGetValue(other, out var profile))
                    {
                        continue;
                    }

                    var isAccepted = relationship.Status == FriendshipStatus.Accepted;
                    presence.TryGetValue(other, out var state);

                    friends.Add(new FriendDto
                    {
                        FriendCode = other,
                        DisplayName = profile.DisplayName,
                        Status = relationship.Status,

                        // Incoming means they asked us, so we owe an answer. Always
                        // false once accepted.
                        IsIncomingRequest = !isAccepted && !isOutgoing,

                        // Presence is withheld until the friendship is accepted. A
                        // pending request must not reveal what somebody is playing.
                        CurrentGameTitle = isAccepted ? state?.CurrentGameTitle : null,
                        IsOnline = isAccepted && state?.IsOnline == true,
                        LastSeenAt = state?.LastSeenAt ?? profile.CreatedAt
                    });
                }

                return Results.Ok(new FriendListResponse { Friends = friends });
            })
            .RequireAuthorization()
            .WithName("GetFriends");

    /// <summary>Maps catalog resolution.</summary>
    /// <param name="app">The application to map onto.</param>
    private static void MapCatalog(WebApplication app) =>
        app.MapPost("/catalog/resolve", async (
                CatalogResolveRequest request,
                ClaimsPrincipal principal,
                ICatalogRepository catalog,
                CancellationToken cancellationToken) =>
            {
                var fingerprint = request.Fingerprint?.Trim().ToLowerInvariant() ?? string.Empty;
                var title = request.Title?.Trim() ?? string.Empty;

                if (fingerprint.Length is < 16 or > 128 || !fingerprint.All(Uri.IsHexDigit))
                {
                    return Results.BadRequest(new ErrorResponse
                    {
                        Error = "invalid_fingerprint",
                        Detail = "A fingerprint must be 16 to 128 hexadecimal characters."
                    });
                }

                if (title.Length is 0 or > 200)
                {
                    return Results.BadRequest(new ErrorResponse
                    {
                        Error = "invalid_title",
                        Detail = "A title of 1 to 200 characters is required."
                    });
                }

                var existing = await catalog
                    .ResolveByFingerprintAsync(fingerprint, cancellationToken)
                    .ConfigureAwait(false);

                var wasCreated = false;

                if (existing is null)
                {
                    // Open creation: a miss creates the entry rather than failing.
                    // Users must not wait for moderation before their library works.
                    var now = DateTimeOffset.UtcNow;

                    existing = await catalog.CreateAsync(
                        new RelayCatalogEntry
                        {
                            CatalogId = CatalogIdPrefix + Guid.NewGuid().ToString("N"),
                            CanonicalTitle = title,
                            Company = request.Company?.Trim(),
                            CreatedAt = now,
                            UpdatedAt = now
                        },
                        fingerprint,
                        cancellationToken).ConfigureAwait(false);

                    wasCreated = true;
                }

                await catalog.RecordOwnershipAsync(
                    principal.GetFriendCode(), existing.CatalogId, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new CatalogResolveResponse
                {
                    CatalogId = existing.CatalogId,
                    CanonicalTitle = existing.CanonicalTitle,
                    WasCreated = wasCreated
                });
            })
            .RequireAuthorization()
            .WithName("ResolveCatalog");

    /// <summary>Maps achievement synchronisation.</summary>
    /// <param name="app">The application to map onto.</param>
    private static void MapSync(WebApplication app) =>
        app.MapPost("/sync/achievements", async (
                AchievementSyncRequest request,
                ClaimsPrincipal principal,
                IAchievementRepository achievements,
                ICatalogRepository catalog,
                CancellationToken cancellationToken) =>
            {
                var caller = principal.GetFriendCode();

                if (request.Unlocks.Count > 500)
                {
                    return Results.BadRequest(new ErrorResponse
                    {
                        Error = "batch_too_large",
                        Detail = "Send at most 500 unlocks per request."
                    });
                }

                // Resolved before storing, so an unlock pushed against an id that
                // has since been merged lands on the surviving entry rather than
                // failing a foreign key or stranding history on a dead identity.
                var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var catalogId in request.Unlocks.Select(unlock => unlock.CatalogId)
                             .Concat(request.IncludeCatalogIds)
                             .Distinct(StringComparer.Ordinal))
                {
                    if (await catalog.ResolveCanonicalAsync(catalogId, cancellationToken).ConfigureAwait(false)
                        is { } entry)
                    {
                        resolved[catalogId] = entry.CatalogId;
                    }
                }

                var toMerge = request.Unlocks
                    .Where(unlock => resolved.ContainsKey(unlock.CatalogId))
                    .Where(unlock => !string.IsNullOrWhiteSpace(unlock.ApiName))
                    .Select(unlock => new RelayUserAchievement
                    {
                        FriendCode = caller,
                        CatalogId = resolved[unlock.CatalogId],
                        ApiName = unlock.ApiName.Trim(),
                        UnlockedAt = unlock.UnlockedAt
                    })
                    .ToArray();

                var accepted = await achievements
                    .MergeAsync(caller, toMerge, cancellationToken)
                    .ConfigureAwait(false);

                var canonicalIds = resolved.Values.Distinct(StringComparer.Ordinal).ToArray();

                var stored = await achievements
                    .GetForUserAsync(caller, canonicalIds, cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new AchievementSyncResponse
                {
                    Accepted = accepted,
                    Unlocks = stored.Select(row => new AchievementUnlockDto
                    {
                        CatalogId = row.CatalogId,
                        ApiName = row.ApiName,
                        UnlockedAt = row.UnlockedAt
                    }).ToArray()
                });
            })
            .RequireAuthorization()
            .WithName("SyncAchievements");
}
