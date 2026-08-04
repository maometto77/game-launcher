using GameLauncher.Shared.Contracts;
using GameLauncher.Shared.Hubs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http.Json;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Hosts the real relay in-process against a throwaway database.
/// </summary>
/// <remarks>
/// Uses the actual <c>Program</c> composition rather than reconstructing it, so
/// a service missing from the relay's own wiring fails these tests too. The
/// database is redirected through configuration, which is the same mechanism a
/// VPS deployment uses — exercising it here proves the setting is honoured.
/// </remarks>
public sealed class RelayTestFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}.db");

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("Relay:Database:ConnectionString", $"Data Source={_databasePath}");
    }

    /// <summary>
    /// Registers a user and returns their credentials.
    /// </summary>
    /// <param name="displayName">Display name to register with.</param>
    /// <returns>The registration response.</returns>
    public async Task<RegisterResponse> RegisterAsync(string displayName)
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            "/register", new RegisterRequest { DisplayName = displayName });

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<RegisterResponse>()
               ?? throw new InvalidOperationException("Registration returned no body.");
    }

    /// <summary>
    /// Opens an authenticated hub connection.
    /// </summary>
    /// <param name="authToken">The device token to authenticate with.</param>
    /// <returns>A started connection the caller must dispose.</returns>
    /// <remarks>
    /// Long polling rather than WebSockets: the in-memory test server needs extra
    /// plumbing for a WebSocket handshake, and the transport is not what these
    /// tests are about. Every hub method, authentication path and fan-out rule is
    /// exercised identically over either.
    /// </remarks>
    public async Task<HubConnection> ConnectAsync(string authToken)
    {
        var url = new Uri(
            Server.BaseAddress,
            $"{PresenceHubContract.Path}?{PresenceHubContract.AccessTokenQueryParameter}={authToken}");

        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // The write-ahead log and shared-memory sidecars must go too, or the temp
        // directory accumulates three files per test run.
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; a stray temp file is not worth failing a green run.
            }
        }
    }
}
