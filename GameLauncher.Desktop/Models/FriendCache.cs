namespace GameLauncher.Desktop.Models;

/// <summary>
/// A locally cached snapshot of a friend, so the Friends UI has something to
/// render before the relay connection is established.
/// </summary>
/// <remarks>
/// This is a cache and never the source of truth. Every field is overwritten by
/// the relay once connected. It exists so that starting the app offline, or on
/// a slow link, still shows a populated friends list marked stale rather than an
/// empty one.
/// </remarks>
public sealed class FriendCache
{
    /// <summary>The friend's public friend code. Primary key.</summary>
    public string FriendCode { get; set; } = string.Empty;

    /// <summary>Last known display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Title of the game the friend was last seen playing, or
    /// <see langword="null"/> if they were not in a game.
    /// </summary>
    public string? LastKnownGame { get; set; }

    /// <summary>When the relay last reported seeing this friend.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Absolute path to a cached avatar image, or <see langword="null"/> for the default.</summary>
    public string? AvatarPath { get; set; }
}
