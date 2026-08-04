using GameLauncher.Desktop.Services.Database;
using GameLauncher.Desktop.Services.Friends;
using GameLauncher.Desktop.Services.Launcher;
using GameLauncher.Desktop.Services.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Owns the launcher's relationship with the relay: registration, connection,
/// presence reporting, and draining the outbound queues.
/// </summary>
/// <remarks>
/// <para>
/// Everything it does is best-effort. Nothing here can prevent the launcher
/// starting or delay the window appearing, because an offline-first launcher
/// that waits on the network is not offline-first.
/// </para>
/// <para>
/// It exists so that no single service has to know the whole sequence —
/// register before connecting, sync after connecting, publish presence when a
/// game starts — while each individual service stays independently testable.
/// </para>
/// </remarks>
public sealed class RelayCoordinatorService : IHostedService, IDisposable
{
    private readonly IRelayHubClient _hub;
    private readonly IRelayIdentityService _identity;
    private readonly IRelaySyncService _sync;
    private readonly IFriendsService _friends;
    private readonly ISettingsService _settings;
    private readonly IGameLaunchService _launcher;
    private readonly IGameRepository _games;
    private readonly ILogger<RelayCoordinatorService> _logger;

    private CancellationTokenSource? _lifetime;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="hub">Live relay connection.</param>
    /// <param name="identity">Establishes which relay is in use and migrates state when it changes.</param>
    /// <param name="sync">Outbound queue drain.</param>
    /// <param name="friends">Friends list, loaded from cache at startup.</param>
    /// <param name="settings">Supplies and stores relay credentials.</param>
    /// <param name="launcher">Raises game start and exit events.</param>
    /// <param name="games">Used to resolve a launched game's catalog identity.</param>
    /// <param name="logger">Logger for coordination diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RelayCoordinatorService(
        IRelayHubClient hub,
        IRelayIdentityService identity,
        IRelaySyncService sync,
        IFriendsService friends,
        ISettingsService settings,
        IGameLaunchService launcher,
        IGameRepository games,
        ILogger<RelayCoordinatorService> logger)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _friends = friends ?? throw new ArgumentNullException(nameof(friends));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Cached friends first, before anything touches the network, so the
        // Friends page has content whether or not the relay ever answers.
        await _friends.LoadFromCacheAsync(cancellationToken).ConfigureAwait(false);

        _launcher.GameStarted += OnGameStarted;
        _launcher.GameExited += OnGameExited;
        _hub.StateChanged += OnConnectionStateChanged;

        // Not awaited. Registration and connection happen in the background; the
        // window opens regardless.
        _ = Task.Run(() => BeginAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _launcher.GameStarted -= OnGameStarted;
        _launcher.GameExited -= OnGameExited;
        _hub.StateChanged -= OnConnectionStateChanged;

        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        await _hub.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers if needed, then starts the connection supervisor.</summary>
    /// <param name="cancellationToken">Cancels startup.</param>
    private async Task BeginAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
            await _hub.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Relay problems must never surface as a startup failure.
            _logger.LogError(ex, "Starting the relay connection failed.");
        }
    }

    /// <summary>
    /// Identifies the relay, selects or creates credentials for it, and migrates
    /// catalog identities if it has changed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <remarks>
    /// Must complete before the connection is started, because which token to
    /// present depends on which relay is answering — and because reusing catalog
    /// ids from a previous relay has to be prevented before anything is pushed.
    /// </remarks>
    private async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        var result = await _identity.EstablishAsync(cancellationToken).ConfigureAwait(false);

        if (result.RelayChanged)
        {
            _logger.LogInformation(
                "Switched to relay {RelayId}; {Count} catalog entries queued for re-resolution.",
                result.RelayId, result.EntriesMarkedForReResolution);
        }
    }

    /// <summary>Runs a sync pass and refreshes friends once connected.</summary>
    /// <param name="sender">The hub client.</param>
    /// <param name="change">The new connection state.</param>
    private void OnConnectionStateChanged(object? sender, RelayConnectionStateChanged change)
    {
        if (change.State != RelayConnectionState.Connected)
        {
            return;
        }

        // This is the offline-to-online transition: everything queued while
        // disconnected is pushed now.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _sync
                    .SynchronizeAsync(_lifetime?.Token ?? CancellationToken.None)
                    .ConfigureAwait(false);

                if (result.DidWork)
                {
                    _logger.LogInformation(
                        "Reconnect sync: {Promoted} catalog entries, {Pushed} unlocks.",
                        result.CatalogEntriesPromoted, result.UnlocksPushed);
                }

                // Presence is re-asserted after reconnecting, because the relay
                // cleared it when the connection dropped.
                await PublishCurrentPresenceAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The reconnect synchronisation pass failed.");
            }
        }, CancellationToken.None);
    }

    /// <summary>Reports a game starting.</summary>
    /// <param name="sender">The launch service.</param>
    /// <param name="e">Details of the session.</param>
    private void OnGameStarted(object? sender, GameSessionEventArgs e) =>
        _ = PublishPresenceAsync(e.Game.Title, e.Game.CatalogId);

    /// <summary>Reports a game ending.</summary>
    /// <param name="sender">The launch service.</param>
    /// <param name="e">Details of the session.</param>
    private void OnGameExited(object? sender, GameSessionEventArgs e) =>
        _ = PublishCurrentPresenceAsync();

    /// <summary>Publishes whatever is running now, or clears presence.</summary>
    private async Task PublishCurrentPresenceAsync()
    {
        var runningId = _launcher.RunningGameIds.FirstOrDefault();

        if (runningId == 0)
        {
            await PublishPresenceAsync(null, null).ConfigureAwait(false);
            return;
        }

        var game = await _games.GetByIdAsync(runningId).ConfigureAwait(false);
        await PublishPresenceAsync(game?.Title, game?.CatalogId).ConfigureAwait(false);
    }

    /// <summary>Sends a presence update, ignoring failure.</summary>
    /// <param name="title">Title being played, or <see langword="null"/>.</param>
    /// <param name="catalogId">Catalog identity of that title, if known.</param>
    private async Task PublishPresenceAsync(string? title, string? catalogId)
    {
        try
        {
            // Presence is ephemeral and never queued: the next update supersedes a
            // missed one, so failing quietly is correct.
            await _hub.UpdatePresenceAsync(title, catalogId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Publishing presence failed.");
        }
    }

    /// <summary>Releases the coordinator's cancellation source.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime?.Dispose();
    }
}
