namespace GameLauncher.Shared.Hubs;

/// <summary>
/// Names and connection details of the relay's presence hub.
/// </summary>
/// <remarks>
/// Client-to-server calls go through SignalR's string-based
/// <c>InvokeAsync</c>/<c>SendAsync</c>, which has no compile-time checking. The
/// method names live here as constants so the client and the relay reference
/// one definition instead of repeating string literals that can silently drift
/// apart.
/// </remarks>
public static class PresenceHubContract
{
    /// <summary>Path the hub is mapped to on the relay, relative to the server root.</summary>
    public const string Path = "/hubs/presence";

    /// <summary>
    /// Name of the query string parameter carrying the caller's auth token.
    /// </summary>
    /// <remarks>
    /// The browser WebSocket API cannot set request headers, so SignalR's
    /// convention is to pass the credential in the query string. The desktop
    /// client is not a browser and sends a proper <c>Authorization</c> header
    /// as well; the relay accepts either.
    /// </remarks>
    public const string AccessTokenQueryParameter = "access_token";

    /// <summary>Server methods a client may invoke.</summary>
    public static class Methods
    {
        /// <summary>Reports the caller's current game, or clears it. Broadcast to accepted friends.</summary>
        public const string UpdatePresence = nameof(UpdatePresence);

        /// <summary>Sends a friend request to another user by friend code.</summary>
        public const string SendFriendRequest = nameof(SendFriendRequest);

        /// <summary>Accepts or rejects a pending inbound friend request.</summary>
        public const string RespondFriendRequest = nameof(RespondFriendRequest);

        /// <summary>Keeps the connection marked live and refreshes the caller's last-seen time.</summary>
        public const string Heartbeat = nameof(Heartbeat);
    }
}
