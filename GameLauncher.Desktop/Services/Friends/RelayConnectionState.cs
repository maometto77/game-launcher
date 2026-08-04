namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// The launcher's relationship with the relay at a point in time.
/// </summary>
/// <remarks>
/// Distinguishes "no relay configured" from "configured but unreachable",
/// because they mean different things to the user: the first is a setting they
/// have not filled in, the second is a problem. Showing "offline" for both would
/// imply something is broken when nothing is.
/// </remarks>
public enum RelayConnectionState
{
    /// <summary>No relay address is configured. Friends are unavailable by choice.</summary>
    Disabled = 0,

    /// <summary>A relay is configured but not currently reachable.</summary>
    Offline = 1,

    /// <summary>Establishing the first connection.</summary>
    Connecting = 2,

    /// <summary>Connected and receiving live updates.</summary>
    Connected = 3,

    /// <summary>The connection dropped and is being re-established.</summary>
    Reconnecting = 4
}

/// <summary>
/// Describes a change in relay connection state.
/// </summary>
/// <param name="State">The new state.</param>
/// <param name="Detail">
/// A short user-facing explanation, or <see langword="null"/> when the state
/// speaks for itself.
/// </param>
public sealed record RelayConnectionStateChanged(RelayConnectionState State, string? Detail = null)
{
    /// <summary>Gets a value indicating whether live updates are flowing.</summary>
    public bool IsLive => State == RelayConnectionState.Connected;

    /// <summary>
    /// Gets a value indicating whether the launcher is working from cached data.
    /// </summary>
    public bool IsUsingCache => State is not RelayConnectionState.Connected;
}
