using System.Windows.Media;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// Extracts an executable's embedded icon for use as placeholder artwork.
/// </summary>
/// <remarks>
/// Implemented against the Win32 icon APIs rather than
/// <c>System.Drawing.Common</c>, which since .NET 6 is Windows-only, carries a
/// GDI+ dependency, and would be pulled in solely to convert a handle into a
/// bitmap that WPF can already produce natively.
/// </remarks>
public interface IIconExtractionService
{
    /// <summary>
    /// Extracts an executable's icon and writes it as a PNG.
    /// </summary>
    /// <param name="executablePath">Absolute path to the executable.</param>
    /// <param name="destinationDirectory">Directory the image is written into. Created if missing.</param>
    /// <param name="baseFileName">
    /// Name for the image without extension. Sanitised before use, so a game
    /// title may be passed directly.
    /// </param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>
    /// The absolute path to the written PNG, or <see langword="null"/> when the
    /// executable carries no icon or it could not be read.
    /// </returns>
    /// <remarks>
    /// Returns null rather than throwing when there is no icon. A game without
    /// embedded artwork is completely ordinary, and the library already renders a
    /// generated tile in that case.
    /// </remarks>
    Task<string?> ExtractToPngAsync(
        string executablePath,
        string destinationDirectory,
        string baseFileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts an executable's icon into a frozen image for immediate display,
    /// without writing anything to disk.
    /// </summary>
    /// <param name="executablePath">Absolute path to the executable.</param>
    /// <returns>
    /// A frozen image, safe to hand to any thread, or <see langword="null"/> when
    /// no icon could be read.
    /// </returns>
    /// <remarks>
    /// Used by the scan results list, which previews many executables the user
    /// may never add. Writing a file per preview would litter the artwork folder
    /// with images for games that were never imported.
    /// </remarks>
    ImageSource? ExtractImage(string executablePath);
}
