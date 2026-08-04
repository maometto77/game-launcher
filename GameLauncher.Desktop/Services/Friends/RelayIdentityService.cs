using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Catalog;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// Default <see cref="IRelayIdentityService"/>.
/// </summary>
public sealed class RelayIdentityService : IRelayIdentityService
{
    private readonly IRelayApiClient _api;
    private readonly ISettingsService _settings;
    private readonly ICatalogRepository _catalog;
    private readonly IAchievementRepository _achievements;
    private readonly IPlaySessionRepository _sessions;
    private readonly IFriendCacheRepository _friendCache;
    private readonly ILogger<RelayIdentityService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="api">Used to ask the relay to identify itself and to register.</param>
    /// <param name="settings">Stores per-relay credentials and the active relay.</param>
    /// <param name="catalog">Catalog persistence, for demoting foreign identities.</param>
    /// <param name="achievements">Achievement persistence, for re-queuing unlocks.</param>
    /// <param name="sessions">Session persistence, for re-queuing playtime.</param>
    /// <param name="friendCache">Friend cache, which is per relay.</param>
    /// <param name="logger">Logger for migration diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RelayIdentityService(
        IRelayApiClient api,
        ISettingsService settings,
        ICatalogRepository catalog,
        IAchievementRepository achievements,
        IPlaySessionRepository sessions,
        IFriendCacheRepository friendCache,
        ILogger<RelayIdentityService> logger)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _achievements = achievements ?? throw new ArgumentNullException(nameof(achievements));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _friendCache = friendCache ?? throw new ArgumentNullException(nameof(friendCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<RelayIdentityResult> EstablishAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;

        if (!settings.HasRelay)
        {
            return RelayIdentityResult.Unreachable;
        }

        RelayInfoOrFailure probe;
        try
        {
            var info = await _api.GetRelayInfoAsync(cancellationToken).ConfigureAwait(false);
            probe = new RelayInfoOrFailure(info.RelayId, null);
        }
        catch (RelayApiException ex)
        {
            // Offline-safe: nothing is migrated on a guess. The launcher keeps
            // whatever identities it has and tries again later.
            _logger.LogDebug(ex, "Could not identify the relay; leaving local state untouched.");
            return RelayIdentityResult.Unreachable;
        }

        var relayId = probe.RelayId!;
        var relayChanged = settings.ActiveRelayId is not null &&
                           !string.Equals(settings.ActiveRelayId, relayId, StringComparison.Ordinal);

        var demoted = 0;

        if (relayChanged)
        {
            demoted = await MigrateToRelayAsync(relayId, cancellationToken).ConfigureAwait(false);

            // Re-read: the migration saved settings, and continuing from the stale
            // copy would undo it.
            settings = _settings.Current;
        }

        var identity = settings.FindIdentity(relayId, settings.RelayUrl);

        if (identity is null)
        {
            identity = await RegisterAsync(relayId, settings, cancellationToken).ConfigureAwait(false);

            if (identity is null)
            {
                return new RelayIdentityResult(relayId, IsReady: false, relayChanged, demoted);
            }

            settings = _settings.Current;
        }

        await BindIdentityAsync(relayId, identity, settings, cancellationToken).ConfigureAwait(false);

        return new RelayIdentityResult(relayId, IsReady: true, relayChanged, demoted);
    }

    /// <summary>
    /// Prepares local state for a relay the launcher has not been using.
    /// </summary>
    /// <param name="relayId">The relay now in use.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <returns>How many catalog entries were demoted.</returns>
    /// <remarks>
    /// <para>
    /// Nothing is deleted. Games, achievements, play sessions, collections and
    /// tags are all untouched: only the identities that were meaningful solely to
    /// the previous relay are reset, and the sync watermarks recorded against it
    /// are cleared so everything is offered to the new relay.
    /// </para>
    /// <para>
    /// The friend cache is cleared because friendships are per relay — showing
    /// somebody's friends from a relay they are no longer using would be showing
    /// them a relationship that does not exist where they now are.
    /// </para>
    /// </remarks>
    private async Task<int> MigrateToRelayAsync(string relayId, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "The configured relay has changed to {RelayId}. Catalog identities from the previous relay " +
            "will be re-resolved; no local data is removed.", relayId);

        var demoted = await _catalog
            .DemoteForeignEntriesAsync(relayId, cancellationToken)
            .ConfigureAwait(false);

        // The new relay has seen none of this history, so every watermark recorded
        // against the old one has to go or the launcher would withhold everything
        // earned so far.
        var unlocks = await _achievements.ResetUnlockSyncStateAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await _sessions.ResetSyncStateAsync(cancellationToken).ConfigureAwait(false);

        await _friendCache.ReplaceAllAsync([], cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Relay migration: {Demoted} catalog entries demoted, {Unlocks} unlocks and {Sessions} sessions re-queued.",
            demoted, unlocks, sessions);

        return demoted;
    }

    /// <summary>Registers with a relay this installation has no credentials for.</summary>
    /// <param name="relayId">The relay to register with.</param>
    /// <param name="settings">Current settings.</param>
    /// <param name="cancellationToken">Cancels registration.</param>
    /// <returns>The new credentials, or <see langword="null"/> when registration failed.</returns>
    private async Task<RelayIdentity?> RegisterAsync(
        string relayId,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _api
                .RegisterAsync(settings.DisplayName, cancellationToken)
                .ConfigureAwait(false);

            var identity = new RelayIdentity
            {
                RelayId = relayId,
                RelayUrl = settings.RelayUrl,
                FriendCode = response.FriendCode,
                AuthToken = response.AuthToken,
                DeviceId = response.DeviceId,
                LastUsedAt = DateTimeOffset.Now
            };

            await _settings.SaveAsync(settings with
            {
                RelayIdentities = [.. settings.RelayIdentities, identity],
                ActiveRelayId = relayId
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Registered with relay {RelayId} as {FriendCode}.", relayId, response.FriendCode);

            return identity;
        }
        catch (RelayApiException ex)
        {
            _logger.LogWarning(ex, "Registering with relay {RelayId} failed; will retry later.", relayId);
            return null;
        }
    }

    /// <summary>
    /// Makes an identity current, binding it to the relay id if it was pending.
    /// </summary>
    /// <param name="relayId">The relay now in use.</param>
    /// <param name="identity">The credentials to activate.</param>
    /// <param name="settings">Current settings.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    private async Task BindIdentityAsync(
        string relayId,
        RelayIdentity identity,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var alreadyBound = string.Equals(identity.RelayId, relayId, StringComparison.Ordinal);
        var alreadyActive = string.Equals(settings.ActiveRelayId, relayId, StringComparison.Ordinal);

        if (alreadyBound && alreadyActive)
        {
            // The common path: same relay as last time, nothing to write.
            return;
        }

        // Credentials carried over from a settings file that predates relay
        // identities are adopted here rather than discarded — the token is still
        // valid, it simply had no relay id recorded against it.
        var bound = identity with
        {
            RelayId = relayId,
            RelayUrl = settings.RelayUrl,
            LastUsedAt = DateTimeOffset.Now
        };

        var identities = settings.RelayIdentities
            .Where(existing => !ReferenceEquals(existing, identity))
            .Where(existing => !string.Equals(existing.RelayId, relayId, StringComparison.Ordinal))
            .Append(bound)
            .ToArray();

        await _settings.SaveAsync(settings with
        {
            RelayIdentities = identities,
            ActiveRelayId = relayId
        }, cancellationToken).ConfigureAwait(false);

        if (!alreadyBound)
        {
            _logger.LogInformation("Bound existing credentials to relay {RelayId}.", relayId);
        }
    }

    /// <summary>Carries the outcome of the relay probe.</summary>
    /// <param name="RelayId">The identity reported, if any.</param>
    /// <param name="Failure">The failure, if the probe did not succeed.</param>
    private sealed record RelayInfoOrFailure(string? RelayId, Exception? Failure);
}
