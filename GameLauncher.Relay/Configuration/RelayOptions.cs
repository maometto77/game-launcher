namespace GameLauncher.Relay.Configuration;

/// <summary>
/// Database engines the relay can run against.
/// </summary>
public enum RelayDatabaseProvider
{
    /// <summary>SQLite. Suitable for self-hosting on a single machine.</summary>
    Sqlite = 0,

    /// <summary>
    /// PostgreSQL. The schema and every query already target it; the connection
    /// factory is not implemented.
    /// </summary>
    Postgres = 1
}

/// <summary>
/// Database configuration.
/// </summary>
public sealed class RelayDatabaseOptions
{
    /// <summary>Which engine to connect to.</summary>
    public RelayDatabaseProvider Provider { get; set; } = RelayDatabaseProvider.Sqlite;

    /// <summary>
    /// Provider-specific connection string.
    /// </summary>
    /// <remarks>
    /// Externalised so the same binaries run on a laptop and a VPS. On a VPS this
    /// should arrive as the <c>Relay__Database__ConnectionString</c> environment
    /// variable rather than in a file.
    /// </remarks>
    public string ConnectionString { get; set; } = "Data Source=gamelauncher-relay.db";
}

/// <summary>
/// Presence tuning.
/// </summary>
public sealed class RelayPresenceOptions
{
    /// <summary>
    /// How often a connected client is expected to send a heartbeat.
    /// </summary>
    /// <remarks>
    /// Only refreshes last-seen. Disconnects are detected by SignalR itself, so
    /// this is not a liveness mechanism and can be generous.
    /// </remarks>
    public int HeartbeatSeconds { get; set; } = 60;
}

/// <summary>
/// Root configuration section for the relay, bound from <c>Relay</c>.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>Name of the configuration section these options bind from.</summary>
    public const string SectionName = "Relay";

    /// <summary>Database configuration.</summary>
    public RelayDatabaseOptions Database { get; set; } = new();

    /// <summary>Presence configuration.</summary>
    public RelayPresenceOptions Presence { get; set; } = new();

    /// <summary>
    /// Origins permitted by CORS.
    /// </summary>
    /// <remarks>
    /// Empty by default. The desktop client is not a browser and is unaffected;
    /// this exists only for a future web front end, and defaulting to open would
    /// be the wrong way round.
    /// </remarks>
    public IList<string> AllowedOrigins { get; set; } = [];
}
