namespace GameLauncher.Desktop.Services.Discovery.Import;

/// <summary>
/// Limits how hard one source is hit, in both directions: how many requests may
/// be in flight, and how closely together they may start.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency alone is not enough. One request at a time in a tight loop is
/// still a request every few milliseconds, which is exactly the traffic pattern
/// that gets an address blocked. Spacing is what makes a crawl polite.
/// </para>
/// <para>
/// Not a general-purpose rate limiter. It does the two things a courteous
/// crawler needs and nothing else, which is why it is thirty lines rather than a
/// package.
/// </para>
/// </remarks>
public sealed class RequestThrottle : IDisposable
{
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _spacingGate = new(1, 1);
    private readonly TimeSpan _minimumInterval;

    private DateTimeOffset _nextAllowedStart = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="throttle">The limits to enforce.</param>
    /// <exception cref="ArgumentNullException"><paramref name="throttle"/> is <see langword="null"/>.</exception>
    public RequestThrottle(SourceThrottle throttle)
    {
        ArgumentNullException.ThrowIfNull(throttle);

        _concurrency = new SemaphoreSlim(Math.Max(1, throttle.MaxConcurrency));
        _minimumInterval = throttle.MinimumInterval < TimeSpan.Zero ? TimeSpan.Zero : throttle.MinimumInterval;
    }

    /// <summary>
    /// Runs an operation once there is room for it.
    /// </summary>
    /// <typeparam name="T">What the operation returns.</typeparam>
    /// <param name="work">The operation to run.</param>
    /// <param name="cancellationToken">Cancels the wait and the operation.</param>
    /// <returns>Whatever the operation returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _concurrency.Dispose();
        _spacingGate.Dispose();
    }

    /// <summary>
    /// Delays until the next request is allowed to start.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <remarks>
    /// The next allowed time is reserved under the gate <em>before</em> the delay
    /// happens, so several waiting callers space themselves out instead of all
    /// reading the same "last request" time and starting together.
    /// </remarks>
    private async Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        if (_minimumInterval <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan delay;

        await _spacingGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var start = _nextAllowedStart > now ? _nextAllowedStart : now;

            delay = start - now;
            _nextAllowedStart = start + _minimumInterval;
        }
        finally
        {
            _spacingGate.Release();
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
