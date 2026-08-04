using GameLauncher.Desktop.Services.Friends;

namespace GameLauncher.Tests.Friends;

/// <summary>
/// Covers the reconnect schedule.
/// </summary>
/// <remarks>
/// Tested directly rather than through a connection, because the property that
/// matters — never giving up, never growing unboundedly — is a pure function of
/// the attempt count and would be almost impossible to observe reliably against
/// a real socket.
/// </remarks>
public sealed class BackoffPolicyTests
{
    [Fact]
    public void Delay_grows_with_each_attempt()
    {
        var policy = new ExponentialBackoffRetryPolicy(
            TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), jitterFactor: 0);

        var first = policy.CalculateDelay(0);
        var second = policy.CalculateDelay(1);
        var third = policy.CalculateDelay(2);

        Assert.True(second > first, "the second delay should exceed the first");
        Assert.True(third > second, "the third delay should exceed the second");
    }

    [Fact]
    public void Delay_never_exceeds_the_configured_maximum()
    {
        var maximum = TimeSpan.FromSeconds(30);
        var policy = new ExponentialBackoffRetryPolicy(TimeSpan.FromSeconds(1), maximum);

        // Includes counts far beyond anything realistic. A launcher left open for
        // days across an outage genuinely reaches these.
        foreach (var attempt in new long[] { 0, 1, 5, 10, 30, 100, 10_000, long.MaxValue })
        {
            var delay = policy.CalculateDelay(attempt);

            Assert.True(delay <= maximum, $"attempt {attempt} produced {delay}, above the maximum");
            Assert.True(delay > TimeSpan.Zero, $"attempt {attempt} produced a non-positive delay");
        }
    }

    [Fact]
    public void A_very_large_attempt_count_does_not_overflow()
    {
        var policy = new ExponentialBackoffRetryPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2));

        // Shifting by an unbounded attempt count would overflow and could yield a
        // negative delay, which Task.Delay rejects.
        var delay = policy.CalculateDelay(long.MaxValue);

        Assert.InRange(delay, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Jitter_spreads_delays_without_exceeding_the_cap()
    {
        var maximum = TimeSpan.FromSeconds(20);
        var policy = new ExponentialBackoffRetryPolicy(TimeSpan.FromSeconds(5), maximum, jitterFactor: 0.5);

        var samples = Enumerable.Range(0, 40).Select(_ => policy.CalculateDelay(6)).ToArray();

        // Jitter exists so that many launchers recovering from one network blip do
        // not all reconnect on the same instant.
        Assert.True(samples.Distinct().Count() > 1, "jitter produced identical delays");
        Assert.All(samples, delay => Assert.True(delay <= maximum));
    }

    [Fact]
    public void Policy_never_gives_up()
    {
        var policy = new ExponentialBackoffRetryPolicy();

        // SignalR's default policy returns null after roughly thirty seconds,
        // which is right for a web page and wrong for a launcher that may sit
        // open across a router reboot.
        foreach (var attempt in new long[] { 0, 10, 1_000, 1_000_000 })
        {
            Assert.NotNull(policy.NextRetryDelay(
                new Microsoft.AspNetCore.SignalR.Client.RetryContext
                {
                    PreviousRetryCount = attempt,
                    ElapsedTime = TimeSpan.FromHours(1)
                }));
        }
    }
}
