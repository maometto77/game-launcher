using GameLauncher.Shared.Contracts;
using GameLauncher.Shared.Enums;
using GameLauncher.Shared.Hubs;
using GameLauncher.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace GameLauncher.Tests.Relay;

/// <summary>
/// End-to-end tests for the presence hub against a real in-process relay.
/// </summary>
public sealed class PresenceHubTests : IAsyncLifetime
{
    /// <summary>
    /// How long to wait for a pushed message before failing.
    /// </summary>
    /// <remarks>
    /// Generous, because these tests assert that something <em>arrives</em>.
    /// Only a genuinely broken fan-out reaches the timeout, so a long wait costs
    /// nothing on a passing run and avoids flakiness on a loaded machine.
    /// </remarks>
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(10);

    private RelayTestFactory _relay = null!;

    public Task InitializeAsync()
    {
        _relay = new RelayTestFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _relay.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Hub_rejects_a_connection_with_no_token()
    {
        // The hub carries [Authorize], so an unauthenticated handshake must fail
        // rather than yielding a connection that silently receives nothing.
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_relay.Server.BaseAddress, PresenceHubContract.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _relay.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Friend_request_reaches_the_addressee_and_acceptance_reaches_the_requester()
    {
        var alice = await _relay.RegisterAsync("Alice");
        var bob = await _relay.RegisterAsync("Bob");

        await using var aliceHub = await _relay.ConnectAsync(alice.AuthToken);
        await using var bobHub = await _relay.ConnectAsync(bob.AuthToken);

        var requestReceived = new TaskCompletionSource<FriendRequestDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var resultReceived = new TaskCompletionSource<FriendRequestResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribed by nameof against the shared interface, exactly as the
        // desktop client does, so a rename breaks the build rather than the app.
        bobHub.On<FriendRequestDto>(
            nameof(IPresenceClient.FriendRequestReceived), dto => requestReceived.TrySetResult(dto));

        aliceHub.On<FriendRequestResultDto>(
            nameof(IPresenceClient.FriendRequestResolved), dto => resultReceived.TrySetResult(dto));

        await aliceHub.InvokeAsync(PresenceHubContract.Methods.SendFriendRequest, bob.FriendCode);

        var request = await WaitAsync(requestReceived.Task);
        Assert.Equal(alice.FriendCode, request.FromFriendCode);
        Assert.Equal("Alice", request.FromDisplayName);

        await bobHub.InvokeAsync(PresenceHubContract.Methods.RespondFriendRequest, alice.FriendCode, true);

        var result = await WaitAsync(resultReceived.Task);
        Assert.True(result.Accepted);
        Assert.Equal(bob.FriendCode, result.FriendCode);

        // Both sides now see an accepted friendship, from a single stored row.
        var aliceFriends = await GetFriendsAsync(alice.AuthToken);
        var bobFriends = await GetFriendsAsync(bob.AuthToken);

        Assert.Equal(FriendshipStatus.Accepted, Assert.Single(aliceFriends).Status);
        Assert.Equal(FriendshipStatus.Accepted, Assert.Single(bobFriends).Status);
    }

    [Fact]
    public async Task Presence_reaches_accepted_friends_only()
    {
        var alice = await _relay.RegisterAsync("Alice");
        var bob = await _relay.RegisterAsync("Bob");
        var stranger = await _relay.RegisterAsync("Stranger");

        await using var aliceHub = await _relay.ConnectAsync(alice.AuthToken);
        await using var bobHub = await _relay.ConnectAsync(bob.AuthToken);
        await using var strangerHub = await _relay.ConnectAsync(stranger.AuthToken);

        await BefriendAsync(aliceHub, bobHub, alice.FriendCode, bob.FriendCode);

        var bobSaw = new TaskCompletionSource<PresenceDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var strangerSaw = new TaskCompletionSource<PresenceDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        bobHub.On<PresenceDto>(nameof(IPresenceClient.PresenceChanged), dto =>
        {
            if (dto.CurrentGameTitle is not null)
            {
                bobSaw.TrySetResult(dto);
            }
        });

        strangerHub.On<PresenceDto>(nameof(IPresenceClient.PresenceChanged), dto => strangerSaw.TrySetResult(dto));

        await aliceHub.InvokeAsync(PresenceHubContract.Methods.UpdatePresence, "Hollow Signal", null);

        var seen = await WaitAsync(bobSaw.Task);
        Assert.Equal(alice.FriendCode, seen.FriendCode);
        Assert.Equal("Hollow Signal", seen.CurrentGameTitle);
        Assert.True(seen.IsOnline);

        // The stranger is not a friend and must never have been sent anything.
        // Waiting briefly and asserting nothing arrived is the only way to test
        // an absence.
        var strangerGotSomething = await Task.WhenAny(
            strangerSaw.Task, Task.Delay(TimeSpan.FromSeconds(2))) == strangerSaw.Task;

        Assert.False(strangerGotSomething, "presence leaked to a user who is not a friend");
    }

    [Fact]
    public async Task Pending_request_does_not_expose_presence()
    {
        var alice = await _relay.RegisterAsync("Alice");
        var bob = await _relay.RegisterAsync("Bob");

        await using var aliceHub = await _relay.ConnectAsync(alice.AuthToken);
        await using var bobHub = await _relay.ConnectAsync(bob.AuthToken);

        await aliceHub.InvokeAsync(PresenceHubContract.Methods.SendFriendRequest, bob.FriendCode);
        await aliceHub.InvokeAsync(PresenceHubContract.Methods.UpdatePresence, "Hollow Signal", null);

        var bobFriends = await GetFriendsAsync(bob.AuthToken);
        var entry = Assert.Single(bobFriends);

        Assert.Equal(FriendshipStatus.Pending, entry.Status);
        Assert.True(entry.IsIncomingRequest);

        // A pending request reveals a display name and nothing else.
        Assert.Null(entry.CurrentGameTitle);
        Assert.False(entry.IsOnline);
    }

    [Fact]
    public async Task A_request_to_an_unknown_code_is_indistinguishable_from_an_invalid_one()
    {
        var alice = await _relay.RegisterAsync("Alice");
        await using var aliceHub = await _relay.ConnectAsync(alice.AuthToken);

        var unknown = await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.InvokeAsync(PresenceHubContract.Methods.SendFriendRequest, "GL-AAAAA-AAAAA"));

        var malformed = await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.InvokeAsync(PresenceHubContract.Methods.SendFriendRequest, "not-a-code"));

        // Identical messages: differing ones would let a caller enumerate which
        // friend codes exist.
        Assert.Equal(
            StripHubPrefix(malformed.Message),
            StripHubPrefix(unknown.Message));
    }

    [Fact]
    public async Task A_user_cannot_accept_a_request_they_sent_themselves()
    {
        var alice = await _relay.RegisterAsync("Alice");
        var bob = await _relay.RegisterAsync("Bob");

        await using var aliceHub = await _relay.ConnectAsync(alice.AuthToken);

        await aliceHub.InvokeAsync(PresenceHubContract.Methods.SendFriendRequest, bob.FriendCode);

        // Only the addressee may answer. Without the direction check this would
        // let anyone befriend anyone unilaterally.
        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.InvokeAsync(PresenceHubContract.Methods.RespondFriendRequest, bob.FriendCode, true));
    }

    [Fact]
    public async Task Disconnecting_marks_the_user_offline_for_their_friends()
    {
        var alice = await _relay.RegisterAsync("Alice");
        var bob = await _relay.RegisterAsync("Bob");

        var aliceHub = await _relay.ConnectAsync(alice.AuthToken);
        await using var bobHub = await _relay.ConnectAsync(bob.AuthToken);

        await BefriendAsync(aliceHub, bobHub, alice.FriendCode, bob.FriendCode);

        var wentOffline = new TaskCompletionSource<PresenceDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        bobHub.On<PresenceDto>(nameof(IPresenceClient.PresenceChanged), dto =>
        {
            if (!dto.IsOnline)
            {
                wentOffline.TrySetResult(dto);
            }
        });

        await aliceHub.DisposeAsync();

        var offline = await WaitAsync(wentOffline.Task);
        Assert.Equal(alice.FriendCode, offline.FriendCode);
        Assert.False(offline.IsOnline);
        Assert.Null(offline.CurrentGameTitle);
    }

    /// <summary>Sends and accepts a friend request between two connected users.</summary>
    private static async Task BefriendAsync(
        HubConnection requesterHub,
        HubConnection addresseeHub,
        string requesterCode,
        string addresseeCode)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = addresseeHub.On<FriendRequestDto>(
            nameof(IPresenceClient.FriendRequestReceived), _ => received.TrySetResult());

        await requesterHub.InvokeAsync(PresenceHubContract.Methods.SendFriendRequest, addresseeCode);
        await WaitAsync(received.Task);

        subscription.Dispose();

        await addresseeHub.InvokeAsync(PresenceHubContract.Methods.RespondFriendRequest, requesterCode, true);
    }

    /// <summary>Fetches a user's friend list over HTTP.</summary>
    private async Task<IReadOnlyList<FriendDto>> GetFriendsAsync(string authToken)
    {
        using var client = _relay.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        var response = await client.GetAsync("/friends");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<FriendListResponse>();
        return payload?.Friends ?? [];
    }

    /// <summary>Awaits a task, failing with a clear message if it does not complete in time.</summary>
    private static async Task<T> WaitAsync<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(MessageTimeout));

        Assert.True(completed == task, "timed out waiting for a hub message");
        return await task;
    }

    /// <summary>Awaits a task, failing with a clear message if it does not complete in time.</summary>
    private static async Task WaitAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(MessageTimeout));

        Assert.True(completed == task, "timed out waiting for a hub message");
        await task;
    }

    /// <summary>
    /// Removes SignalR's wrapper text so two hub errors can be compared on their
    /// message alone.
    /// </summary>
    private static string StripHubPrefix(string message)
    {
        const string Marker = "HubException: ";
        var index = message.IndexOf(Marker, StringComparison.Ordinal);
        return index >= 0 ? message[(index + Marker.Length)..] : message;
    }
}
