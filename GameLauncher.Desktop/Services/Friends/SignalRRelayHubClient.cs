using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Shared.Contracts;
using GameLauncher.Shared.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// Default <see cref="IRelayHubClient"/>, built on SignalR.
/// </summary>
/// <remarks>
/// <para>
/// Connection management is split in two because SignalR only covers half of it.
/// <c>WithAutomaticReconnect</c> handles a connection that drops <em>after</em>
/// succeeding once; it does nothing for a first connection that never succeeds,
/// which is the common case when the launcher starts before the relay is
/// reachable. The supervisor loop here covers that, and resumes if the
/// connection closes permanently.
/// </para>
/// <para>
/// Both paths share one backoff policy, so a launcher left open across an outage
/// settles into the same steady retry either way.
/// </para>
/// </remarks>
public sealed class SignalRRelayHubClient : IRelayHubClient
{
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SignalRRelayHubClient> _logger;
    private readonly ExponentialBackoffRetryPolicy _retryPolicy = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HubConnection? _connection;
    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisor;
    private RelayConnectionState _state = RelayConnectionState.Disabled;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="settings">Supplies the relay address and token.</param>
    /// <param name="dispatcher">Marshals events onto the UI thread.</param>
    /// <param name="logger">Logger for connection diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SignalRRelayHubClient(
        ISettingsService settings,
        IUiDispatcher dispatcher,
        ILogger<SignalRRelayHubClient> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public RelayConnectionState State => _state;

    /// <inheritdoc />
    public event EventHandler<RelayConnectionStateChanged>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<PresenceDto>? PresenceChanged;

    /// <inheritdoc />
    public event EventHandler<FriendRequestDto>? FriendRequestReceived;

    /// <inheritdoc />
    public event EventHandler<FriendRequestResultDto>? FriendRequestResolved;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_supervisor is not null)
        {
            return Task.CompletedTask;
        }

        _supervisorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Not awaited: the caller must not wait on the network. Startup continues
        // and the UI shows whatever state the supervisor reaches.
        _supervisor = Task.Run(() => SuperviseAsync(_supervisorCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_supervisorCts is not null)
        {
            await _supervisorCts.CancelAsync().ConfigureAwait(false);
        }

        if (_supervisor is not null)
        {
            // The supervisor swallows cancellation, so this cannot throw.
            await _supervisor.ConfigureAwait(false);
            _supervisor = null;
        }

        await TearDownConnectionAsync().ConfigureAwait(false);
        SetState(RelayConnectionState.Disabled);
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePresenceAsync(
        string? gameTitle,
        string? catalogId,
        CancellationToken cancellationToken = default)
    {
        var connection = _connection;

        if (connection is null || connection.State != HubConnectionState.Connected)
        {
            return false;
        }

        try
        {
            await connection.InvokeAsync(
                PresenceHubContract.Methods.UpdatePresence, gameTitle, catalogId, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Presence is ephemeral: the next update supersedes this one, so a
            // failure is logged and forgotten rather than surfaced or retried.
            _logger.LogDebug(ex, "Presence update did not reach the relay.");
            return false;
        }
    }

    /// <inheritdoc />
    public Task SendFriendRequestAsync(string friendCode, CancellationToken cancellationToken = default) =>
        InvokeAsync(PresenceHubContract.Methods.SendFriendRequest, [friendCode], cancellationToken);

    /// <inheritdoc />
    public Task RespondFriendRequestAsync(
        string friendCode,
        bool accept,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(PresenceHubContract.Methods.RespondFriendRequest, [friendCode, accept], cancellationToken);

    /// <summary>Invokes a hub method, translating failures into a usable exception.</summary>
    /// <param name="method">Hub method name.</param>
    /// <param name="arguments">Arguments to pass.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private async Task InvokeAsync(string method, object?[] arguments, CancellationToken cancellationToken)
    {
        var connection = _connection;

        if (connection is null || connection.State != HubConnectionState.Connected)
        {
            throw new RelayApiException(
                "You are not connected to the relay. This will work once the connection is restored.",
                isTransient: true);
        }

        try
        {
            await connection.InvokeCoreAsync(method, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.AspNetCore.SignalR.HubException ex)
        {
            // The relay rejected it deliberately — an unknown friend code, a
            // duplicate request. Its message is written for the user, so it is
            // passed through rather than replaced.
            throw new RelayApiException(ex.Message, isTransient: false, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RelayApiException("The relay could not be reached.", isTransient: true, ex);
        }
    }

    /// <summary>
    /// Keeps a connection alive for as long as the launcher wants one.
    /// </summary>
    /// <param name="cancellationToken">Stops supervision.</param>
    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var attempt = 0L;

        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _settings.Current;

            // Re-read every iteration, so configuring a relay in Settings starts
            // the connection without a restart.
            if (!settings.HasRelay || !settings.IsRegistered)
            {
                SetState(RelayConnectionState.Disabled);
                await DelayAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                SetState(attempt == 0 ? RelayConnectionState.Connecting : RelayConnectionState.Reconnecting);

                await ConnectAsync(cancellationToken).ConfigureAwait(false);

                attempt = 0;
                SetState(RelayConnectionState.Connected);

                // Parks here until the connection closes for good. SignalR handles
                // intermediate drops itself; this only resumes when it gives up or
                // the connection is closed deliberately.
                await WaitForCloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Relay connection attempt {Attempt} failed.", attempt + 1);
            }

            await TearDownConnectionAsync().ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            SetState(RelayConnectionState.Offline, "The relay is not reachable.");

            var delay = _retryPolicy.CalculateDelay(attempt);
            attempt++;

            _logger.LogDebug("Retrying the relay connection in {Delay}.", delay);
            await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }

        SetState(RelayConnectionState.Disabled);
    }

    /// <summary>Builds and starts a hub connection.</summary>
    /// <param name="cancellationToken">Cancels the connect.</param>
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await TearDownConnectionAsync().ConfigureAwait(false);

            var settings = _settings.Current;
            var baseAddress = new Uri(settings.RelayUrl!, UriKind.Absolute);
            var hubUrl = new Uri(baseAddress, PresenceHubContract.Path.TrimStart('/'));

            var connection = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    // Read per attempt rather than captured, so a token obtained by
                    // registering after startup is picked up on the next connect
                    // without restarting the app. This is the whole of what token
                    // refresh means here: relay tokens do not expire.
                    options.AccessTokenProvider = () =>
                        Task.FromResult(_settings.Current.ActiveAuthToken);
                })
                .WithAutomaticReconnect(_retryPolicy)
                .Build();

            connection.Reconnecting += error =>
            {
                SetState(RelayConnectionState.Reconnecting, "Reconnecting to the relay…");
                _logger.LogDebug(error, "Relay connection dropped; reconnecting.");
                return Task.CompletedTask;
            };

            connection.Reconnected += _ =>
            {
                SetState(RelayConnectionState.Connected);
                return Task.CompletedTask;
            };

            RegisterHandlers(connection);

            _connection = connection;
            await connection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Subscribes to the server-to-client contract.</summary>
    /// <param name="connection">The connection to subscribe on.</param>
    /// <remarks>
    /// Subscribed by <c>nameof</c> against <see cref="IPresenceClient"/>, the
    /// same interface the relay implements, so a renamed method is a build error
    /// rather than a handler that silently never fires.
    /// </remarks>
    private void RegisterHandlers(HubConnection connection)
    {
        connection.On<PresenceDto>(
            nameof(IPresenceClient.PresenceChanged),
            dto => RaiseOnUi(() => PresenceChanged?.Invoke(this, dto)));

        connection.On<FriendRequestDto>(
            nameof(IPresenceClient.FriendRequestReceived),
            dto => RaiseOnUi(() => FriendRequestReceived?.Invoke(this, dto)));

        connection.On<FriendRequestResultDto>(
            nameof(IPresenceClient.FriendRequestResolved),
            dto => RaiseOnUi(() => FriendRequestResolved?.Invoke(this, dto)));
    }

    /// <summary>Waits until the connection closes.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private async Task WaitForCloseAsync(CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task OnClosed(Exception? error)
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        }

        connection.Closed += OnClosed;

        try
        {
            await using var registration = cancellationToken.Register(() => closed.TrySetResult())
                .ConfigureAwait(false);

            await closed.Task.ConfigureAwait(false);
        }
        finally
        {
            connection.Closed -= OnClosed;
        }
    }

    /// <summary>Disposes the current connection, if any.</summary>
    private async Task TearDownConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the relay connection failed.");
        }
    }

    /// <summary>Waits, treating cancellation as a normal exit rather than an error.</summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>Publishes a state change, if it is actually a change.</summary>
    /// <param name="state">The new state.</param>
    /// <param name="detail">Optional user-facing explanation.</param>
    private void SetState(RelayConnectionState state, string? detail = null)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        _logger.LogInformation("Relay connection state: {State}.", state);

        RaiseOnUi(() => StateChanged?.Invoke(this, new RelayConnectionStateChanged(state, detail)));
    }

    /// <summary>Raises an event on the UI thread.</summary>
    /// <param name="action">The handler invocation.</param>
    /// <remarks>
    /// Hub callbacks arrive on a thread pool thread, and subscribers update bound
    /// collections. Marshalling here means every consumer is safe by default.
    /// </remarks>
    private void RaiseOnUi(Action action)
    {
        try
        {
            _dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not tear down the connection supervisor.
            _logger.LogError(ex, "A relay event handler threw.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync().ConfigureAwait(false);

        _supervisorCts?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Releases the connection without waiting for it to close cleanly.
    /// </summary>
    /// <remarks>
    /// Required alongside <see cref="DisposeAsync"/> because the DI container is
    /// disposed synchronously, and a singleton implementing only
    /// <see cref="IAsyncDisposable"/> makes that throw — which would surface as a
    /// crash on every application exit.
    /// <para>
    /// This cancels supervision and lets the connection tear down in the
    /// background rather than blocking the shutdown path on a network round trip.
    /// The process is ending; the relay notices the dropped socket regardless.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _supervisorCts?.Cancel();

        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Background disposal of the relay connection failed.");
                }
            });
        }

        _supervisorCts?.Dispose();
        _gate.Dispose();
    }
}
