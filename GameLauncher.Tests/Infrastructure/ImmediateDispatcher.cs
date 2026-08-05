using GameLauncher.Desktop.Infrastructure;

namespace GameLauncher.Tests.Infrastructure;

/// <summary>
/// Runs dispatched work inline.
/// </summary>
/// <remarks>
/// The real dispatcher posts to the WPF message loop, which no xunit test is
/// pumping — a service raising an event from a background thread would block
/// forever waiting for a loop that never runs. Substituting this keeps the code
/// under test on the calling thread while leaving the marshalling call itself
/// exercised.
/// </remarks>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public bool IsOnUiThread => true;

    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }
}
