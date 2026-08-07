namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Collects things that happened during startup which the user needs to be told
/// about once a window exists.
/// </summary>
/// <remarks>
/// Startup runs before there is any interface, so a hosted service has nowhere to
/// put a message. Writing it only to the log means the user never learns that,
/// for example, their library was reset. This holds such messages until the shell
/// can show them.
/// </remarks>
public interface IStartupNotices
{
    /// <summary>Gets the notices raised during startup, in order.</summary>
    IReadOnlyList<string> Messages { get; }

    /// <summary>Adds a notice.</summary>
    /// <param name="message">A complete, user-facing sentence.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is null or blank.</exception>
    void Add(string message);

    /// <summary>Removes every notice, once they have been shown.</summary>
    void Clear();
}

/// <summary>
/// Default <see cref="IStartupNotices"/>.
/// </summary>
public sealed class StartupNotices : IStartupNotices
{
    // Startup services run on the host's thread and the shell reads on the
    // interface thread, so the list is guarded.
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    /// <inheritdoc />
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Add(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A notice must have a message.", nameof(message));
        }

        lock (_gate)
        {
            _messages.Add(message);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
        }
    }
}
