using GameLauncher.Relay.Configuration;
using GameLauncher.Relay.Data;
using GameLauncher.Relay.Data.Repositories;
using GameLauncher.Relay.Endpoints;
using GameLauncher.Relay.Hubs;
using GameLauncher.Relay.Security;
using GameLauncher.Shared.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
//
// Bound from the "Relay" section, which standard ASP.NET Core precedence lets an
// environment variable override (Relay__Database__ConnectionString). That is how
// a connection string should reach a VPS — never in a committed file.
// ---------------------------------------------------------------------------
builder.Services
    .AddOptions<RelayOptions>()
    .Bind(builder.Configuration.GetSection(RelayOptions.SectionName))
    .ValidateOnStart();

var relayOptions = builder.Configuration
    .GetSection(RelayOptions.SectionName)
    .Get<RelayOptions>() ?? new RelayOptions();

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IRelayConnectionFactory>(serviceProvider =>
    relayOptions.Database.Provider switch
    {
        RelayDatabaseProvider.Sqlite =>
            ActivatorUtilities.CreateInstance<SqliteRelayConnectionFactory>(serviceProvider),

        // The schema and every query already target PostgreSQL; only the factory
        // is missing. Failing loudly at startup is better than starting and then
        // failing on the first request.
        RelayDatabaseProvider.Postgres => throw new NotSupportedException(
            "The PostgreSQL connection factory is not implemented. Add the Npgsql package and an " +
            "IRelayConnectionFactory for it; the schema and queries need no changes."),

        _ => throw new InvalidOperationException(
            $"Unknown database provider '{relayOptions.Database.Provider}'.")
    });

builder.Services.AddSingleton<RelayDatabaseInitializer>();

builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IDeviceRepository, DeviceRepository>();
builder.Services.AddSingleton<IFriendshipRepository, FriendshipRepository>();
builder.Services.AddSingleton<IPresenceRepository, PresenceRepository>();
builder.Services.AddSingleton<ICatalogRepository, CatalogRepository>();
builder.Services.AddSingleton<IAchievementRepository, AchievementRepository>();

// ---------------------------------------------------------------------------
// Security
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<ITokenService, TokenService>();

builder.Services
    .AddAuthentication(RelayAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, RelayAuthenticationHandler>(
        RelayAuthenticationHandler.SchemeName, configureOptions: null);

builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// Real-time
// ---------------------------------------------------------------------------
builder.Services.AddSignalR();

// Addresses messages to a person rather than a connection, so every device a
// user has online receives them.
builder.Services.AddSingleton<IUserIdProvider, FriendCodeUserIdProvider>();
builder.Services.AddSingleton<PresenceTracker>();

// ---------------------------------------------------------------------------
// CORS
//
// Only configured when origins are listed. The desktop client is not a browser
// and is unaffected; defaulting to open would be the wrong way round.
// ---------------------------------------------------------------------------
const string CorsPolicyName = "RelayCors";

if (relayOptions.AllowedOrigins.Count > 0)
{
    builder.Services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy
        .WithOrigins([.. relayOptions.AllowedOrigins])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
}

// ---------------------------------------------------------------------------
// Reverse proxy
//
// On a VPS this sits behind Caddy or Nginx terminating TLS, so every request
// arrives over plain HTTP from a loopback address. Without this the application
// sees that literally: it believes the scheme is http and the client address is
// the proxy's, which makes any absolute URL it generates wrong and every request
// look like it came from the same machine.
//
// The known-proxies list is cleared deliberately. In a container the proxy's
// address is assigned by the container network and is not knowable in advance;
// the protection that matters is that nothing but the proxy can reach the
// application's port, which the Compose file arranges by not publishing it.
// ---------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

// ---------------------------------------------------------------------------
// Schema
//
// Migrated before the first request is served, so no endpoint can meet a table
// that does not exist yet.
// ---------------------------------------------------------------------------
await app.Services.GetRequiredService<RelayDatabaseInitializer>()
    .InitializeAsync()
    .ConfigureAwait(false);

if (relayOptions.AllowedOrigins.Count > 0)
{
    app.UseCors(CorsPolicyName);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapRelayEndpoints();
app.MapHub<PresenceHub>(PresenceHubContract.Path);

app.Run();

/// <summary>
/// Entry point marker, exposed so integration tests can host the relay through
/// <c>WebApplicationFactory</c>.
/// </summary>
/// <remarks>
/// A top-level-statements program compiles to an internal <c>Program</c> class.
/// This partial declaration makes it public without introducing a second entry
/// point, which is the supported way to test a minimal-API application in place
/// rather than reconstructing its composition in the test.
/// </remarks>
public partial class Program;
