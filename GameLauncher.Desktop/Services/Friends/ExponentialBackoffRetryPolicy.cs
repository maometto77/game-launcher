using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameLauncher.Desktop.Services.Friends;

/// <summary>
/// Reconnect schedule for the relay connection: exponential backoff with
/// jitter, capped, and never giving up.
/// </summary>
/// <remarks>
/// <para>
/// Never returning <see langword="null"/> is deliberate. SignalR's default
/// policy stops retrying after about thirty seconds, which is the right
/// behaviour for a web page and the wrong one for a launcher that may sit open
/// for days across a router reboot or a laptop suspend. Here, "the relay is
/// down" is a temporary condition to keep waiting out, not a failure to
/// surrender to.
/// </para>
/// <para>
/// The delay is capped so that a long outage settles into a steady poll rather
/// than growing towards hours, and jittered so that many launchers coming back
/// after the same network blip do not all reconnect on the same instant.
/// </para>
/// </remarks>
public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly double _jitterFactor;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="initialDelay">Delay before the first retry.</param>
    /// <param name="maximumDelay">Ceiling the backoff grows towards.</param>
    /// <param name="jitterFactor">
    /// Fraction of the computed delay applied as random spread, between zero and
    /// one.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A delay is negative, the maximum is below the initial delay, or the jitter
    /// factor is outside zero to one.
    /// </exception>
    public ExponentialBackoffRetryPolicy(
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null,
        double jitterFactor = 0.25)
    {
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(2);
        _maximumDelay = maximumDelay ?? TimeSpan.FromMinutes(2);
        _jitterFactor = jitterFactor;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_initialDelay.TotalMilliseconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(_maximumDelay, _initialDelay);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterFactor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterFactor, 1d);
    }

    /// <inheritdoc />
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        ArgumentNullException.ThrowIfNull(retryContext);
        return CalculateDelay(retryContext.PreviousRetryCount);
    }

    /// <summary>
    /// Calculates the delay before a given retry attempt.
    /// </summary>
    /// <param name="previousRetryCount">How many retries have already happened.</param>
    /// <returns>The delay to wait, always positive and never above the maximum.</returns>
    /// <remarks>
    /// Exposed separately from <see cref="NextRetryDelay"/> so the schedule can be
    /// tested without constructing SignalR's <see cref="RetryContext"/>.
    /// </remarks>
    public TimeSpan CalculateDelay(long previousRetryCount)
    {
        if (previousRetryCount < 0)
        {
            previousRetryCount = 0;
        }

        // Capped before the shift rather than after: 2^63 overflows, and a
        // connection that has been retrying for days would reach that.
        const int MaximumShift = 20;
        var shift = (int)Math.Min(previousRetryCount, MaximumShift);

        var scaled = _initialDelay.TotalMilliseconds * Math.Pow(2, shift);
        var capped = Math.Min(scaled, _maximumDelay.TotalMilliseconds);

        if (_jitterFactor <= 0)
        {
            return TimeSpan.FromMilliseconds(capped);
        }

        // Jitter is subtracted rather than added, so the result can never exceed
        // the configured maximum.
        var spread = capped * _jitterFactor;
        var offset = RandomNumberGenerator.GetInt32(0, (int)Math.Max(1, spread));

        return TimeSpan.FromMilliseconds(Math.Max(_initialDelay.TotalMilliseconds / 2, capped - offset));
    }
}
