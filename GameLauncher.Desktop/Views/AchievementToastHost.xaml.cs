using System.Windows.Controls;

namespace GameLauncher.Desktop.Views;

/// <summary>
/// Overlay that announces an achievement as it is earned.
/// </summary>
/// <remarks>
/// Purely a renderer. What appears, in what order and for how long is decided by
/// the notification service behind its view model; this control knows only how to
/// draw whichever announcement is current.
/// </remarks>
public partial class AchievementToastHost : UserControl
{
    /// <summary>Initialises a new instance.</summary>
    public AchievementToastHost() => InitializeComponent();
}
