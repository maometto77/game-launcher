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

    /// <summary>
    /// Whether the discovery catalogue refreshes itself in the background.
    /// </summary>
    /// <remarks>
    /// Off leaves whatever has already been imported browsable. Discovery is an
    /// addition to the launcher, never a prerequisite for it — the library, the
    /// installs and the achievements all work with this switched off.
    /// </remarks>
    [JsonPropertyName("discoveryEnabled")]
    public bool DiscoveryEnabled { get; init; }

    /// <summary>
    /// Internet Archive collections to import.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A setting rather than a constant because it is the whole scope of what
    /// gets imported. <c>softwarelibrary_msdos_games</c> holds about 8 900
    /// items; the parent <c>softwarelibrary</c> holds over 230 000 and is far too
    /// broad to take wholesale.
    /// </para>
    /// <para>
    /// Empty means the source has nothing to do and reports itself unavailable,
    /// which is not an error.
    /// </para>
    /// </remarks>
    [JsonPropertyName("internetArchiveCollections")]
    public IReadOnlyList<string> InternetArchiveCollections { get; init; } =
        ["softwarelibrary_msdos_games"];

    /// <summary>
    /// Whether MyAbandonware is imported alongside the Internet Archive.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DiscoveryEnabled"/> and off by default. That
    /// site's <c>robots.txt</c> disallows its download paths, so the source
    /// contributes metadata only — worth having for titles, genres and
    /// screenshots, but a different proposition from a source that supplies
    /// installable files, and worth an explicit decision.
    /// </remarks>
    [JsonPropertyName("myAbandonwareEnabled")]
    public bool MyAbandonwareEnabled { get; init; }

    /// <summary>
    /// Address of a shared catalogue feed, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single JSON document listing what one group has gathered, so everyone
    /// pointed at it sees the same catalogue. Unset by default: this is somebody's
    /// own server, and which one is not something a launcher can guess.
    /// </para>
    /// <para>
    /// Unlike the other sources, this one is not a site being read from the
    /// outside — whoever publishes it hosts the files too. That is why it is the
    /// only source that can state a SHA-256 it actually computed, and why it
    /// outranks the rest when two sources describe the same game.
    /// </para>
    /// </remarks>
    [JsonPropertyName("sharedCatalogUrl")]
    public string? SharedCatalogUrl { get; init; }

    /// <summary>
    /// An Internet Archive uploader whose items should be imported, or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The search index stores an uploader as an email address, which is what
    /// this must be — a screen name will not match. It is combined with
    /// <see cref="InternetArchiveCollections"/> rather than replacing it, so an
    /// import can cover curated collections and one person's uploads at once.
    /// </para>
    /// <para>
    /// An uploader's items are whatever that person chose to upload. Unlike a
    /// curated collection, nothing about them is vouched for by the Archive, so
    /// this is left empty by default and is an explicit choice.
    /// </para>
    /// </remarks>
    [JsonPropertyName("internetArchiveUploader")]
    public string? InternetArchiveUploader { get; init; }

    /// <summary>
    /// Whether downloads should use <c>aria2c</c> when it is available.
    /// </summary>
    /// <remarks>
    /// Off by default. Turning it on lets the launcher start an external process,
    /// which is a decision worth making explicitly rather than inheriting because
    /// a binary happens to be on the path. With it off — or with aria2c missing —
    /// the built-in HttpClient engine handles every download exactly as before.
    /// </remarks>
    [JsonPropertyName("aria2Enabled")]
    public bool Aria2Enabled { get; init; }

    /// <summary>
    /// Extra directories to watch for local achievement files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Added to the three the launcher already knows — Goldberg's saves folder
    /// and the RUNE and CODEX folders under Public Documents — rather than
    /// replacing them. Each must be a directory holding one folder per Steam
    /// application id, because that folder name is the only place the id appears.
    /// </para>
    /// <para>
    /// Exists because these locations are conventions rather than standards, and
    /// the next writer to appear will invent a fourth. A setting means that costs
    /// the user a path rather than a release.
    /// </para>
    /// </remarks>
    [JsonPropertyName("achievementWatchRoots")]
    public IReadOnlyList<string> AchievementWatchRoots { get; init; } = [];

    /// <summary>
    /// Full path to <c>aria2c</c>, or <see langword="null"/> to find it on the
    /// system path.
    /// </summary>
    [JsonPropertyName("aria2ExecutablePath")]
    public string? Aria2ExecutablePath { get; init; }

    /// <summary>
    /// How many connections aria2 opens per download.
    /// </summary>
    /// <remarks>
    /// The reason to use aria2 at all: one stream is limited by per-connection
    /// shaping, and several are not. Clamped when applied — the Archive asks
    /// clients not to open more than a handful.
    /// </remarks>
    [JsonPropertyName("aria2Connections")]
    public int Aria2Connections { get; init; } = 4;

    /// <summary>Hours between background catalogue refreshes.</summary>
    [JsonPropertyName("discoveryRefreshHours")]
    public int DiscoveryRefreshHours { get; init; } = 24;

    /// <summary>Largest size the cached catalogue artwork may reach, in megabytes.</summary>
    [JsonPropertyName("discoveryImageCacheMegabytes")]
    public int DiscoveryImageCacheMegabytes { get; init; } = 500;

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
