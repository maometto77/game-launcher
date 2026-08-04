using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GameLauncher.Desktop.ViewModels;

namespace GameLauncher.Desktop.Views;

/// <summary>
/// The application shell window: sidebar, header, and the hosted page.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// DWM attribute that switches the window's title bar to its dark variant.
    /// </summary>
    /// <remarks>
    /// Value 20 applies from Windows 10 build 18985 onwards. Builds 18362-18984
    /// used 19. Both are attempted, because a wrong attribute id is rejected
    /// harmlessly by DWM.
    /// </remarks>
    private const int DwmwaUseImmersiveDarkMode = 20;

    private const int DwmwaUseImmersiveDarkModeBefore19H1 = 19;

    /// <summary>
    /// Initialises a new instance and assigns its view model.
    /// </summary>
    /// <param name="viewModel">Shell view model supplied by the container.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public MainWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Gets the display version shown at the foot of the sidebar.
    /// </summary>
    /// <remarks>
    /// Deliberately an instance property: the sidebar binds to it with a
    /// <c>RelativeSource</c> walk to the window, and a WPF binding path cannot
    /// resolve a static member that way.
    /// </remarks>
    public string Version =>
        $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    /// <summary>
    /// Asks DWM to render this window's title bar in dark mode so the system
    /// chrome matches the application's theme.
    /// </summary>
    /// <remarks>
    /// Best-effort. On a Windows build that does not support the attribute the
    /// call simply fails and the window keeps the default light title bar, which
    /// is cosmetic only.
    /// </remarks>
    private void ApplyDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;

        try
        {
            if (NativeMethods.DwmSetWindowAttribute(
                    handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                NativeMethods.DwmSetWindowAttribute(
                    handle, DwmwaUseImmersiveDarkModeBefore19H1, ref enabled, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // dwmapi.dll is present on every supported Windows version; if it is
            // somehow missing, a light title bar is not worth failing startup over.
        }
    }

    /// <summary>
    /// P/Invoke declarations used by the shell window.
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>Sets a Desktop Window Manager attribute on a window.</summary>
        /// <param name="hwnd">Target window handle.</param>
        /// <param name="attribute">Attribute identifier.</param>
        /// <param name="value">Attribute value.</param>
        /// <param name="size">Size of <paramref name="value"/> in bytes.</param>
        /// <returns>Zero on success; a non-zero HRESULT otherwise.</returns>
        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
