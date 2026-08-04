using GameLauncher.Shared.Enums;

namespace GameLauncher.Relay.Data.Repositories;

/// <summary>A registered person.</summary>
public sealed class RelayUser
{
    /// <summary>Public identity, unique across the relay.</summary>
    public string FriendCode { get; set; } = string.Empty;

    /// <summary>Name shown to friends.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>When the user registered.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the user record last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One machine belonging to a user.</summary>
/// <remarks>
/// The credential lives here rather than on the user, so a second machine can be
/// added and an individual one revoked without disturbing the other.
/// </remarks>
public sealed class RelayDevice
{
    /// <summary>Opaque device identifier. Not a secret.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>The user this device belongs to.</summary>
    public string FriendCode { get; set; } = string.Empty;

    /// <summary>SHA-256 of the device's bearer token. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Human-readable label, for a future device list.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>When the device was registered.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the device last presented its token.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When the device was revoked, or <see langword="null"/> while active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A directed friendship record, created by the requester.</summary>
public sealed class RelayFriendship
{
    /// <summary>The user who sent the request.</summary>
    public string UserFriendCode { get; set; } = string.Empty;

    /// <summary>The user who received it.</summary>
    public string FriendFriendCode { get; set; } = string.Empty;

    /// <summary>Current state of the relationship.</summary>
    public FriendshipStatus Status { get; set; }

    /// <summary>When the request was sent.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was answered, or <see langword="null"/> while pending.</summary>
    public DateTimeOffset? RespondedAt { get; set; }
}

/// <summary>A user's presence, aggregated across their devices.</summary>
public sealed class RelayPresence
{
    /// <summary>The user this presence describes.</summary>
    public string FriendCode { get; set; } = string.Empty;

    /// <summary>Title of the game being played, or <see langword="null"/>.</summary>
    public string? CurrentGameTitle { get; set; }

    /// <summary>
    /// Shared catalog identity of that game, when the client knows it.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose: a game whose catalog entry has not been resolved yet
    /// must still show as being played.
    /// </remarks>
    public string? CurrentGameCatalogId { get; set; }

    /// <summary>Whether any of the user's devices holds a live connection.</summary>
    public bool IsOnline { get; set; }

    /// <summary>When the user was last seen.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>A shared game title.</summary>
public sealed class RelayCatalogEntry
{
    /// <summary>Immutable shared identity.</summary>
    public string CatalogId { get; set; } = string.Empty;

    /// <summary>The relay's canonical title.</summary>
    public string CanonicalTitle { get; set; } = string.Empty;

    /// <summary>Publisher, when a client reported one.</summary>
    public string? Company { get; set; }

    /// <summary>The entry this was merged into, or <see langword="null"/> when canonical.</summary>
    public string? SupersededByCatalogId { get; set; }

    /// <summary>When the entry was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the entry last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One user's record of having earned an achievement.</summary>
public sealed class RelayUserAchievement
{
    /// <summary>The user who earned it.</summary>
    public string FriendCode { get; set; } = string.Empty;

    /// <summary>The title it belongs to.</summary>
    public string CatalogId { get; set; } = string.Empty;

    /// <summary>Stable handle of the achievement within that title.</summary>
    public string ApiName { get; set; } = string.Empty;

    /// <summary>When it was earned.</summary>
    public DateTimeOffset UnlockedAt { get; set; }
}
