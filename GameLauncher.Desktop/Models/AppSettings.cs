using System.Text.Json.Serialization;

namespace GameLauncher.Desktop.Models;

/// <summary>
/// The available colour themes.
/// </summary>
/// <remarks>
/// Both are dark. A light theme is a pure palette swap now that no view
/// hard-codes a colour, but none is shipped because its contrast could not be
/// verified without looking at it.
/// </remarks>
public enum AppTheme
{
    /// <summary>The default blue-grey dark theme.</summary>
    Dark = 0,

    /// <summary>A deeper, near-black variant for OLED displays and dim rooms.</summary>
    Midnight = 1
}

/// <summary>
/// Credentials held for one relay.
/// </summary>
/// <remarks>
/// Identity is per relay, not per installation. A friend code issued by one
/// relay means nothing to another, so switching relays cannot simply overwrite
/// it — that would lose the friendships built up on the first one. Keeping a
/// record per relay makes switching back restore the original identity intact.
/// </remarks>
public sealed record RelayIdentity
{
    /// <summary>
    /// The relay's instance identity, or <see langword="null"/> when it has not
    /// been discovered yet.
    /// </summary>
    /// <remarks>
    /// Null only for credentials carried over from a settings file written
    /// before relays reported an identity. The first successful
    /// <c>/relay-info</c> call for the matching address binds them.
    /// </remarks>
    [JsonPropertyName("relayId")]
    public string? RelayId { get; init; }

    /// <summary>The address this relay was last reached at.</summary>
    /// <remarks>Used only to bind a pending identity; the relay id is authoritative.</remarks>
    [JsonPropertyName("relayUrl")]
    public string? RelayUrl { get; init; }

    /// <summary>The friend code this relay issued.</summary>
    [JsonPropertyName("friendCode")]
    public required string FriendCode { get; init; }

    /// <summary>The device token this relay issued. A secret.</summary>
    [JsonPropertyName("authToken")]
    public required string AuthToken { get; init; }

    /// <summary>Identifier of this machine's device record on that relay.</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    /// <summary>When these credentials were last used.</summary>
    [JsonPropertyName("lastUsedAt")]
    public DateTimeOffset LastUsedAt { get; init; }
}

/// <summary>
/// User settings, persisted as JSON alongside the library database.
/// </summary>
/// <remarks>
/// Kept in a file rather than the database because these are read before
/// anything else — including the relay identity needed to connect — and because
/// a user can sensibly inspect or hand-edit them.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>The settings schema version this file was written by.</summary>
    /// <remarks>
    /// Present from the start so that a future change of shape can be migrated
    /// rather than guessed at. Discarding settings on upgrade would silently lose
    /// somebody's friend code.
    /// </remarks>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Folders searched when scanning for games.</summary>
    [JsonPropertyName("libraryFolders")]
    public IReadOnlyList<string> LibraryFolders { get; init; } = [];

    /// <summary>Whether to scan the library folders each time the app starts.</summary>
    /// <remarks>
    /// A scan only ever produces candidates for review; nothing is added to the
    /// library without the user selecting it, whether the scan was manual or
    /// automatic.
    /// </remarks>
    [JsonPropertyName("autoScanOnStartup")]
    public bool AutoScanOnStartup { get; init; }

    /// <summary>The selected colour theme.</summary>
    [JsonPropertyName("theme")]
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Base address of the presence relay, or <see langword="null"/> when offline.</summary>
    [JsonPropertyName("relayUrl")]
    public string? RelayUrl { get; init; }

    /// <summary>This installation's public friend code.</summary>
    [JsonPropertyName("friendCode")]
    public string FriendCode { get; init; } = string.Empty;

    /// <summary>The name shown to friends.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Bearer token authenticating this client to the relay.
    /// </summary>
    /// <remarks>
    /// Issued once at registration and unrecoverable if lost, which is why it
    /// lives in the settings file rather than being re-requested. It is a secret:
    /// the settings page shows it masked and never logs it.
    /// </remarks>
    [JsonPropertyName("relayAuthToken")]
    public string? RelayAuthToken { get; init; }

    /// <summary>
    /// Identifier of this machine's device record on the relay.
    /// </summary>
    /// <remarks>
    /// Not a secret. Stamped onto each <see cref="PlaySession"/> so that a future
    /// multi-device merge can tell two concurrent sessions apart from one session
    /// reported twice.
    /// </remarks>
    [JsonPropertyName("relayDeviceId")]
    public string? RelayDeviceId { get; init; }

    /// <summary>
    /// The relay these settings are currently pointed at, by instance identity.
    /// </summary>
    /// <remarks>
    /// Compared against what the configured relay reports. A mismatch means the
    /// launcher has been pointed at a different relay, which requires migrating
    /// catalog identities rather than silently reusing ids from the old one.
    /// </remarks>
    [JsonPropertyName("activeRelayId")]
    public string? ActiveRelayId { get; init; }

    /// <summary>Credentials held for every relay this installation has used.</summary>
    [JsonPropertyName("relayIdentities")]
    public IReadOnlyList<RelayIdentity> RelayIdentities { get; init; } = [];

    /// <summary>
    /// API key for SteamGridDB, or <see langword="null"/> when artwork lookup is
    /// not configured.
    /// </summary>
    /// <remarks>
    /// A secret, though a low-value one: it identifies the requester to a public
    /// artwork index and grants nothing else. The settings page masks it and it is
    /// never logged, for the same reason the relay token is not. Absent, artwork
    /// lookup is simply unavailable and games render generated tiles.
    /// </remarks>
    [JsonPropertyName("steamGridDbApiKey")]
    public string? SteamGridDbApiKey { get; init; }

    /// <summary>Gets a value indicating whether a relay has been configured.</summary>
    [JsonIgnore]
    public bool HasRelay => !string.IsNullOrWhiteSpace(RelayUrl);

    /// <summary>Gets the credentials for the active relay, if any.</summary>
    [JsonIgnore]
    public RelayIdentity? ActiveIdentity => ActiveRelayId is null
        ? null
        : RelayIdentities.FirstOrDefault(identity =>
            string.Equals(identity.RelayId, ActiveRelayId, StringComparison.Ordinal));

    /// <summary>Gets a value indicating whether this client has registered with the active relay.</summary>
    [JsonIgnore]
    public bool IsRegistered => !string.IsNullOrWhiteSpace(ActiveIdentity?.AuthToken);

    /// <summary>Gets the token to present to the active relay, if any.</summary>
    [JsonIgnore]
    public string? ActiveAuthToken => ActiveIdentity?.AuthToken;

    /// <summary>
    /// Gets the friend code to display: the active relay's, or the local one when
    /// no relay is in use.
    /// </summary>
    /// <remarks>
    /// The relay assigns the code others can actually use, so it wins whenever
    /// there is one. The locally generated code exists so the Friends page has
    /// something to show before a relay is configured.
    /// </remarks>
    [JsonIgnore]
    public string EffectiveFriendCode => ActiveIdentity?.FriendCode ?? FriendCode;

    /// <summary>Gets the device identifier for the active relay, if any.</summary>
    [JsonIgnore]
    public string? ActiveDeviceId => ActiveIdentity?.DeviceId;

    /// <summary>
    /// Finds stored credentials for a relay, by identity or by address.
    /// </summary>
    /// <param name="relayId">The relay's instance identity.</param>
    /// <param name="relayUrl">The address it was reached at.</param>
    /// <returns>The matching credentials, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The address is only a fallback for credentials carried over from an older
    /// settings file that predates relay identities. Matching on identity first
    /// means a relay that has moved host is still recognised.
    /// </remarks>
    public RelayIdentity? FindIdentity(string? relayId, string? relayUrl)
    {
        if (!string.IsNullOrWhiteSpace(relayId))
        {
            var byId = RelayIdentities.FirstOrDefault(identity =>
                string.Equals(identity.RelayId, relayId, StringComparison.Ordinal));

            if (byId is not null)
            {
                return byId;
            }
        }

        if (string.IsNullOrWhiteSpace(relayUrl))
        {
            return null;
        }

        return RelayIdentities.FirstOrDefault(identity =>
            identity.RelayId is null &&
            string.Equals(identity.RelayUrl, relayUrl, StringComparison.OrdinalIgnoreCase));
    }
}
