using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Notifications;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// Presents one earned achievement as a toast.
/// </summary>
/// <remarks>
/// A hidden achievement's details are shown in full here: by the time a toast
/// appears the achievement has been earned, which is exactly when concealment
/// ends.
/// </remarks>
public sealed class AchievementToastViewModel
{
    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="notification">The achievement that was earned.</param>
    /// <exception cref="ArgumentNullException"><paramref name="notification"/> is <see langword="null"/>.</exception>
    public AchievementToastViewModel(AchievementNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        Title = notification.Definition.Title;
        Description = notification.Definition.Description;
        IconPath = notification.Definition.IconPath;
        GameTitle = notification.Game?.Title;
    }

    /// <summary>Gets the achievement's name.</summary>
    public string Title { get; }

    /// <summary>Gets the achievement's description.</summary>
    public string Description { get; }

    /// <summary>Gets the achievement's icon, or <see langword="null"/> for the placeholder.</summary>
    public string? IconPath { get; }

    /// <summary>Gets the owning game's title, or <see langword="null"/> for a library-wide achievement.</summary>
    public string? GameTitle { get; }

    /// <summary>Gets the heading shown above the achievement name.</summary>
    public string Heading => GameTitle is null
        ? "Achievement unlocked"
        : $"Achievement unlocked — {GameTitle}";
}

/// <summary>
/// Hosts the achievement toast overlay.
/// </summary>
/// <remarks>
/// <para>
/// Subscribes to <see cref="IAchievementNotificationService"/> and renders
/// whatever it says is current. It holds no queue and no timer of its own —
/// ordering and dwell are the service's job — so this type is a projection and
/// nothing more.
/// </para>
/// <para>
/// It never touches the achievement engine. The only thing that can put a toast
/// on screen is a genuine unlock travelling engine → notification service → here.
/// </para>
/// </remarks>
public sealed partial class AchievementToastHostViewModel : ObservableObject, IDisposable
{
    private readonly IAchievementNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    [ObservableProperty]
    private AchievementToastViewModel? _current;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string? _pendingText;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="notifications">Supplies the announcement to show.</param>
    /// <param name="dispatcher">Marshals announcements onto the UI thread.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AchievementToastHostViewModel(
        IAchievementNotificationService notifications,
        IUiDispatcher dispatcher)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _notifications.CurrentChanged += OnCurrentChanged;
    }

    /// <summary>Dismisses the toast on screen and moves to the next.</summary>
    [RelayCommand]
    private void Dismiss() => _notifications.DismissCurrent();

    /// <summary>
    /// Reflects the service's state onto bindable properties.
    /// </summary>
    /// <param name="sender">The notification service.</param>
    /// <param name="e">The announcement now current, and how many follow it.</param>
    /// <remarks>
    /// The service raises this from its own pump, so the update is marshalled
    /// rather than assumed to arrive on the UI thread. Dispatching runs inline
    /// when the caller is already there, so this costs nothing in the case where
    /// it was not needed.
    /// </remarks>
    private void OnCurrentChanged(object? sender, AchievementNotificationChangedEventArgs e)
    {
        _dispatcher.Invoke(() =>
        {
            Current = e.Current is null ? null : new AchievementToastViewModel(e.Current);
            IsVisible = e.Current is not null;

            PendingText = e.PendingCount > 0
                ? $"+{e.PendingCount} more"
                : null;
        });
    }

    /// <summary>Detaches from the notification service.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifications.CurrentChanged -= OnCurrentChanged;
    }
}
