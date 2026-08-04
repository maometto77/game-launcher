using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// Default <see cref="IIconExtractionService"/>, built on the Win32 icon APIs.
/// </summary>
/// <remarks>
/// <para>
/// Two extraction paths are attempted in order. <c>PrivateExtractIcons</c> is
/// tried first because it accepts an explicit size and can return the 256-pixel
/// image modern executables embed; <c>ExtractIconEx</c> is the fallback and
/// returns the system large icon, which is 32 pixels on a standard-DPI display.
/// The order matters for how the library looks: a 32-pixel icon stretched across
/// a 150x225 cover tile is visibly blurred, whereas the 256-pixel image is
/// sharp.
/// </para>
/// <para>
/// Every handle obtained from either API is released with <c>DestroyIcon</c>.
/// Icon handles are a finite per-session GDI resource, and a folder scan can
/// extract hundreds in a few seconds.
/// </para>
/// </remarks>
public sealed class IconExtractionService : IIconExtractionService
{
    /// <summary>Preferred icon size in pixels, matching the largest image Windows executables usually embed.</summary>
    private const int PreferredIconSize = 256;

    private readonly ILogger<IconExtractionService> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="logger">Logger for extraction diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public IconExtractionService(ILogger<IconExtractionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string?> ExtractToPngAsync(
        string executablePath,
        string destinationDirectory,
        string baseFileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var image = ExtractImage(executablePath);
        if (image is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Directory.CreateDirectory(destinationDirectory);

            var safeName = Sanitize(baseFileName);
            var path = Path.Combine(destinationDirectory, $"{safeName}.png");

            // Distinct file per game even when two share a title, so importing
            // "Setup" twice cannot have the second overwrite the first's artwork.
            if (File.Exists(path))
            {
                path = Path.Combine(
                    destinationDirectory,
                    $"{safeName}-{Guid.NewGuid().ToString("N")[..8]}.png");
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create((BitmapSource)image));

            await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }

            _logger.LogDebug("Extracted icon from {Executable} to {Path}.", executablePath, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not write the extracted icon for {Executable}.", executablePath);
            return null;
        }
    }

    /// <inheritdoc />
    public ImageSource? ExtractImage(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        var handle = IntPtr.Zero;

        try
        {
            handle = ExtractLargest(executablePath);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // Frozen so the image can be handed to the UI thread from the
            // background thread a scan runs on.
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not convert the icon of {Executable}.", executablePath);
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }

    /// <summary>
    /// Obtains the highest-resolution icon handle available for a file.
    /// </summary>
    /// <param name="executablePath">Absolute path to the executable.</param>
    /// <returns>An icon handle the caller must destroy, or <see cref="IntPtr.Zero"/>.</returns>
    private IntPtr ExtractLargest(string executablePath)
    {
        // Preferred path: ask for a specific, large size.
        try
        {
            var handles = new IntPtr[1];
            var ids = new int[1];

            var extracted = NativeMethods.PrivateExtractIcons(
                executablePath, 0, PreferredIconSize, PreferredIconSize, handles, ids, 1, 0);

            if (extracted > 0 && handles[0] != IntPtr.Zero)
            {
                return handles[0];
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            // Undocumented-but-ubiquitous export; if it is ever absent, fall
            // through to the documented API rather than failing.
            _logger.LogDebug(ex, "PrivateExtractIcons is unavailable; using ExtractIconEx.");
        }

        // Documented fallback: the system large icon, then the small one.
        var large = new IntPtr[1];
        var small = new IntPtr[1];

        var count = NativeMethods.ExtractIconEx(executablePath, 0, large, small, 1);
        if (count <= 0)
        {
            return IntPtr.Zero;
        }

        if (large[0] != IntPtr.Zero)
        {
            // Only one handle is returned to the caller, so the other must not leak.
            if (small[0] != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(small[0]);
            }

            return large[0];
        }

        return small[0];
    }

    /// <summary>
    /// Reduces arbitrary text to something usable as a file name.
    /// </summary>
    /// <param name="value">Candidate name, typically a game title.</param>
    /// <returns>A non-empty name containing no path-invalid characters.</returns>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "icon";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value.Trim())
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var result = builder.ToString().Trim('.', ' ');

        // Leaves room for the extension and a de-duplicating suffix inside the
        // 255-character limit most Windows file systems impose.
        if (result.Length > 100)
        {
            result = result[..100];
        }

        return string.IsNullOrWhiteSpace(result) ? "icon" : result;
    }

    /// <summary>
    /// P/Invoke declarations for the Win32 icon APIs.
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// Extracts icons at the system's large and small sizes.
        /// </summary>
        /// <param name="fileName">File to extract from.</param>
        /// <param name="iconIndex">Zero-based index of the first icon.</param>
        /// <param name="largeIcons">Receives large icon handles.</param>
        /// <param name="smallIcons">Receives small icon handles.</param>
        /// <param name="iconCount">Number of icons to extract.</param>
        /// <returns>The number of icons extracted, or zero on failure.</returns>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int ExtractIconEx(
            string fileName,
            int iconIndex,
            IntPtr[] largeIcons,
            IntPtr[] smallIcons,
            int iconCount);

        /// <summary>
        /// Extracts icons at an explicitly requested pixel size.
        /// </summary>
        /// <param name="fileName">File to extract from.</param>
        /// <param name="iconIndex">Zero-based index of the first icon.</param>
        /// <param name="width">Requested width in pixels.</param>
        /// <param name="height">Requested height in pixels.</param>
        /// <param name="icons">Receives icon handles.</param>
        /// <param name="iconIds">Receives resource identifiers.</param>
        /// <param name="iconCount">Number of icons to extract.</param>
        /// <param name="flags">Reserved; pass zero.</param>
        /// <returns>The number of icons extracted, or zero on failure.</returns>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int PrivateExtractIcons(
            string fileName,
            int iconIndex,
            int width,
            int height,
            IntPtr[] icons,
            int[] iconIds,
            int iconCount,
            uint flags);

        /// <summary>Releases an icon handle.</summary>
        /// <param name="handle">The handle to destroy.</param>
        /// <returns><see langword="true"/> on success.</returns>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr handle);
    }
}
