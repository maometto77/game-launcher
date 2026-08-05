using GameLauncher.Desktop.Services.Achievements;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Notifications;

/// <summary>
/// Default <see cref="IAchievementNotificationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// A hosted service as well as a notifier, so that it is subscribed before the
/// watcher runs its startup pass. Subscribing lazily — when the shell window is
/// first built — would silently drop any achievement earned by that pass, which
/// is precisely the moment a milestone crossed while the launcher was closed gets
/// awarded.
/// </para>
/// <para>
/// Announcements are drained by a single pump, so two unlocks in one evaluation
/// pass are shown one after the other rather than one on top of the other.
/// </para>
/// </remarks>
public sealed class AchievementNotificationService : IAchievementNotificationService, IHostedService, IDisposable
{
    /// <summary>How long a single announcement stays on screen.</summary>
    private static readonly TimeSpan DefaultDwell = TimeSpan.FromSeconds(5);

    /// <summary>How long each announcement stays on screen while a backlog is waiting.</summary>
    /// <remarks>
    /// A library-wide pass can earn a dozen achievements at once. At the full
    /// dwell that would be a minute of toasts; shortening once a queue forms keeps
    /// the whole run brief without dropping any of them.
    /// </remarks>
    private static readonly TimeSpan BacklogDwell = TimeSpan.FromSeconds(2);

    /// <summary>Queue length at which the shorter dwell takes over.</summary>
    private const int BacklogThreshold = 3;

    private readonly IAchievementEngine _engine;
    private readonly ILogger<AchievementNotificationService> _logger;

    // A plain object rather than System.Threading.Lock, which is .NET 9 and later.
    private readonly object _gate = new();
    private readonly Queue<AchievementNotification> _queue = new();

    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _dismiss;
    private AchievementNotification? _current;
    private int _pendingCount;
    private bool _pumping;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="engine">Raises the unlock events this service announces.</param>
    /// <param name="logger">Logger for announcement diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AchievementNotificationService(
        IAchievementEngine engine,
        ILogger<AchievementNotificationService> logger)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Raised only when the announcement on screen genuinely changes. The pump is
    /// its sole publisher: an arriving unlock queues silently rather than
    /// re-raising for the one already showing, so a subscriber counting
    /// announcements counts each exactly once.
    /// </remarks>
    public event EventHandler<AchievementNotificationChangedEventArgs>? CurrentChanged;

    /// <inheritdoc />
    public AchievementNotification? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pendingCount;
            }
        }
    }

    /// <summary>
    /// Gets or sets how long one announcement stays on screen.
    /// </summary>
    /// <remarks>
    /// Internal so tests can run the pump in milliseconds rather than waiting out
    /// five real seconds per announcement.
    /// </remarks>
    internal TimeSpan Dwell { get; set; } = DefaultDwell;

    /// <summary>Gets or sets the dwell used once a backlog has formed.</summary>
    internal TimeSpan BacklogDwellTime { get; set; } = BacklogDwell;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _engine.AchievementUnlocked += OnAchievementUnlocked;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.AchievementUnlocked -= OnAchievementUnlocked;
        _lifetime?.Cancel();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void DismissCurrent()
    {
        CancellationTokenSource? dismiss;

        lock (_gate)
        {
            dismiss = _dismiss;
        }

        try
        {
            // Cancelled outside the lock deliberately: cancellation runs the
            // waiting pump's continuation, which needs the lock itself.
            dismiss?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The announcement ended on its own between the read and the cancel.
        }
    }

    /// <summary>
    /// Queues a newly earned achievement for announcement.
    /// </summary>
    /// <param name="sender">The engine.</param>
    /// <param name="e">The achievement that was earned.</param>
    /// <remarks>
    /// The engine raises this only on the transition from locked to unlocked, so
    /// repeated evaluation of an achievement that stays earned queues nothing.
    /// </remarks>
    private void OnAchievementUnlocked(object? sender, AchievementUnlockedEventArgs e)
    {
        var notification = new AchievementNotification(e.Definition, e.Game, e.UnlockedAt);
        var lifetime = _lifetime?.Token ?? CancellationToken.None;

        bool start;

        lock (_gate)
        {
            _queue.Enqueue(notification);

            // Only one pump ever runs. A second would race the first for the
            // queue and put two announcements on screen together.
            start = !_pumping;

            if (start)
            {
                _pumping = true;
            }
        }

        // Deliberately silent when a pump is already running. Publishing here
        // would re-raise CurrentChanged for the announcement already on screen —
        // harmless to a binding, but it would make every subscriber that counts
        // announcements report the same one several times over.
        if (start)
        {
            _ = Task.Run(() => PumpAsync(lifetime), CancellationToken.None);
        }
    }

    /// <summary>
    /// Shows queued announcements one at a time until the queue is empty.
    /// </summary>
    /// <param name="lifetime">Cancelled when the application shuts down.</param>
    private async Task PumpAsync(CancellationToken lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                AchievementNotification? next;
                int pending;

                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _pumping = false;
                        _current = null;
                        _pendingCount = 0;
                        next = null;
                        pending = 0;
                    }
                    else
                    {
                        next = _queue.Dequeue();
                        pending = _queue.Count;
                        _current = next;
                        _pendingCount = pending;
                    }
                }

                RaiseCurrentChanged();

                if (next is null)
                {
                    return;
                }

                _logger.LogDebug(
                    "Announcing achievement {ApiName} with {Pending} queued behind it.",
                    next.Definition.ApiName, pending);

                await DwellAsync(pending, lifetime).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // A pump that throws would leave a toast stuck on screen forever and
            // every later unlock unannounced.
            _logger.LogError(ex, "The achievement announcement pump stopped unexpectedly.");
        }
        finally
        {
            if (lifetime.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _pumping = false;
                    _current = null;
                    _pendingCount = 0;
                    _queue.Clear();
                }

                RaiseCurrentChanged();
            }
        }
    }

    /// <summary>
    /// Waits out one announcement, returning early if it is dismissed.
    /// </summary>
    /// <param name="pending">How many announcements are queued behind this one.</param>
    /// <param name="lifetime">Cancelled when the application shuts down.</param>
    private async Task DwellAsync(int pending, CancellationToken lifetime)
    {
        var duration = pending >= BacklogThreshold ? BacklogDwellTime : Dwell;

        using var dismiss = new CancellationTokenSource();

        lock (_gate)
        {
            _dismiss = dismiss;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, dismiss.Token);

        try
        {
            await Task.Delay(duration, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dismissed by the user, or the application is shutting down. Either
            // way the loop decides what happens next.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_dismiss, dismiss))
                {
                    _dismiss = null;
                }
            }
        }
    }

    /// <summary>
    /// Publishes the current state to subscribers.
    /// </summary>
    /// <remarks>
    /// State is read under the lock at the moment of raising rather than passed in
    /// by the caller. A pump finishing at the same instant another starts would
    /// otherwise be able to publish a stale "nothing showing" after the new
    /// announcement had already been published.
    /// </remarks>
    private void RaiseCurrentChanged()
    {
        AchievementNotification? current;
        int pending;

        lock (_gate)
        {
            current = _current;
            pending = _pendingCount;
        }

        CurrentChanged?.Invoke(this, new AchievementNotificationChangedEventArgs(current, pending));
    }

    /// <summary>Unsubscribes and releases the lifetime token source.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _engine.AchievementUnlocked -= OnAchievementUnlocked;

        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
    }
}
