using System.Diagnostics;
using System.Globalization;
using System.Text;
using GameLauncher.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Library;

/// <summary>
/// Default <see cref="IExecutableInspector"/>.
/// </summary>
/// <remarks>
/// Reads the version resource through <see cref="FileVersionInfo"/> and the
/// architecture and subsystem directly from the PE header. Parsing the header by
/// hand rather than loading the image means an executable is never mapped into
/// this process just to describe it, which matters when the file being inspected
/// came from an archive the user downloaded.
/// </remarks>
public sealed class ExecutableInspector : IExecutableInspector
{
    /// <summary>Bytes read from the front of the file; enough to cover the DOS stub and PE headers.</summary>
    private const int HeaderBufferSize = 1024;

    private const ushort DosSignature = 0x5A4D;  // "MZ"
    private const uint PeSignature = 0x00004550; // "PE\0\0"

    private const ushort MachineI386 = 0x014C;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;

    /// <summary>
    /// Name fragments that identify a file as something other than a game.
    /// </summary>
    /// <remarks>
    /// Matched as substrings of the file name without extension. Deliberately
    /// excludes "launcher": for a great many games the launcher <em>is</em> the
    /// entry point the user wants, so filtering it would hide the very thing they
    /// are looking for.
    /// </remarks>
    private static readonly string[] NonGameFragments =
    [
        "unins", "uninstall", "setup", "install",
        "vcredist", "vc_redist", "dxsetup", "dxwebsetup", "directx",
        "dotnetfx", "netfx", "ndp4", "oalinst", "openal",
        "crashhandler", "crashreport", "crashsender", "errorreporter",
        "prereqsetup", "physx", "werfault", "burstdebuginformation",
        "redist", "repair", "cleanup", "activation"
    ];

    /// <summary>
    /// Trailing build tokens stripped when deriving a title from a file name.
    /// </summary>
    /// <remarks>
    /// Unreal Engine ships its games as <c>MyGame-Win64-Shipping.exe</c>, and
    /// similar suffixes are common elsewhere. Without this the library would be
    /// full of entries called "My Game Win64 Shipping".
    /// </remarks>
    private static readonly string[] BuildSuffixTokens =
    [
        "win64", "win32", "winx64", "x64", "x86", "amd64",
        "shipping", "release", "retail", "final", "debug", "development"
    ];

    private readonly ILogger<ExecutableInspector> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="logger">Logger for inspection diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public ExecutableInspector(ILogger<ExecutableInspector> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ExecutableInfo> InspectAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The executable does not exist.", executablePath);
        }

        return await Task.Run(() =>
        {
            var fileName = Path.GetFileName(executablePath);
            var fileInfo = new FileInfo(executablePath);

            string? productName = null;
            string? fileDescription = null;
            string? companyName = null;
            string? fileVersion = null;

            try
            {
                var version = FileVersionInfo.GetVersionInfo(executablePath);
                productName = Clean(version.ProductName);
                fileDescription = Clean(version.FileDescription);
                companyName = Clean(version.CompanyName);
                fileVersion = Clean(version.FileVersion);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Version resources are optional and frequently absent; the file
                // name alone is still enough to add the game.
                _logger.LogDebug(ex, "No readable version resource on {Path}.", executablePath);
            }

            var (architecture, subsystem, isValid) = ReadPeHeader(executablePath, cancellationToken);

            var title = SuggestTitle(fileName, productName, fileDescription);

            return new ExecutableInfo(
                executablePath,
                fileName,
                title,
                productName,
                fileDescription,
                companyName,
                fileVersion,
                fileInfo.Length,
                architecture,
                subsystem,
                isValid);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExecutableValidation> ValidateAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return ExecutableValidation.Fail("No executable has been set for this game.");
        }

        if (!File.Exists(executablePath))
        {
            return ExecutableValidation.Fail(
                $"The executable could not be found at {executablePath}. It may have been moved or uninstalled.");
        }

        ExecutableInfo info;
        try
        {
            info = await InspectAsync(executablePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExecutableValidation.Fail($"The executable could not be read: {ex.Message}");
        }

        if (!info.IsValidExecutable)
        {
            return ExecutableValidation.Fail(
                "This file is not a valid Windows executable, so it cannot be launched.");
        }

        // Architecture is a warning rather than a block: Windows itself will
        // refuse an incompatible binary with a clearer message than we can give,
        // and being wrong here would stop a game that actually runs.
        if (info.Architecture == ExecutableArchitecture.Arm64 &&
            !OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return ExecutableValidation.Warn("This is an ARM64 build and may not run on this machine.");
        }

        if (IsKnownNonGame(info.FileName))
        {
            return ExecutableValidation.Warn(
                $"{info.FileName} looks like an installer or support tool rather than a game.");
        }

        return ExecutableValidation.Ok;
    }

    /// <inheritdoc />
    public bool IsKnownNonGame(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        return NonGameFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads architecture and subsystem from the PE header.
    /// </summary>
    /// <param name="path">Absolute path to the file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The architecture, the subsystem, and whether the file is a well-formed
    /// portable executable.
    /// </returns>
    /// <remarks>
    /// Layout: the DOS header holds the offset of the PE signature at 0x3C; the
    /// COFF header follows it with the machine type; the optional header begins
    /// 24 bytes after the signature and carries the subsystem 68 bytes in. That
    /// last offset is the same for PE32 and PE32+, because the extra eight bytes
    /// PE32+ spends on a 64-bit image base are offset by the <c>BaseOfData</c>
    /// field it drops.
    /// </remarks>
    private (ExecutableArchitecture Architecture, ExecutableSubsystem Subsystem, bool IsValid) ReadPeHeader(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HeaderBufferSize, FileOptions.SequentialScan);

            var buffer = new byte[HeaderBufferSize];
            var read = stream.Read(buffer, 0, buffer.Length);

            cancellationToken.ThrowIfCancellationRequested();

            // Smallest possible PE is far larger than this; anything shorter is
            // not worth parsing.
            if (read < 64 || BitConverter.ToUInt16(buffer, 0) != DosSignature)
            {
                return (ExecutableArchitecture.Unknown, ExecutableSubsystem.Unknown, false);
            }

            var peOffset = BitConverter.ToInt32(buffer, 0x3C);

            // The signature, COFF header and the subsystem field must all lie
            // inside what was read.
            if (peOffset <= 0 || peOffset + 24 + 70 > read)
            {
                return (ExecutableArchitecture.Unknown, ExecutableSubsystem.Unknown, false);
            }

            if (BitConverter.ToUInt32(buffer, peOffset) != PeSignature)
            {
                return (ExecutableArchitecture.Unknown, ExecutableSubsystem.Unknown, false);
            }

            var machine = BitConverter.ToUInt16(buffer, peOffset + 4);
            var architecture = machine switch
            {
                MachineI386 => ExecutableArchitecture.X86,
                MachineAmd64 => ExecutableArchitecture.X64,
                MachineArm64 => ExecutableArchitecture.Arm64,
                _ => ExecutableArchitecture.Unknown
            };

            var subsystemValue = BitConverter.ToUInt16(buffer, peOffset + 24 + 68);
            var subsystem = subsystemValue switch
            {
                2 => ExecutableSubsystem.WindowsGui,
                3 => ExecutableSubsystem.WindowsConsole,
                _ => ExecutableSubsystem.Unknown
            };

            return (architecture, subsystem, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the PE header of {Path}.", path);
            return (ExecutableArchitecture.Unknown, ExecutableSubsystem.Unknown, false);
        }
    }

    /// <summary>
    /// Picks the best available display title for a game.
    /// </summary>
    /// <param name="fileName">The executable's file name.</param>
    /// <param name="productName">Product name from the version resource.</param>
    /// <param name="fileDescription">File description from the version resource.</param>
    /// <returns>A human-readable title, never empty.</returns>
    /// <remarks>
    /// Product name is preferred because it is what publishers actually fill in
    /// with the game's name. File description is the usual fallback. The file
    /// name is last because it needs the most repair to be presentable.
    /// </remarks>
    internal static string SuggestTitle(string fileName, string? productName, string? fileDescription)
    {
        if (IsUsableTitle(productName))
        {
            return productName!.Trim();
        }

        if (IsUsableTitle(fileDescription))
        {
            return fileDescription!.Trim();
        }

        return PrettifyFileName(fileName);
    }

    /// <summary>
    /// Determines whether a version-resource string is worth using as a title.
    /// </summary>
    /// <param name="value">The candidate string.</param>
    /// <returns><see langword="true"/> when it is present and not a placeholder.</returns>
    /// <remarks>
    /// Engines and toolchains leave generic values behind — a Unity game whose
    /// developer never set the product name reports "Unity Player" or the engine
    /// version. Those are worse than the file name.
    /// </remarks>
    private static bool IsUsableTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
        {
            return false;
        }

        var trimmed = value.Trim();

        string[] placeholders =
        [
            "unity player", "unityplayer", "default company", "defaultcompany",
            "product name", "productname", "gamemaker", "application",
            "godot engine", "unreal engine", "ue4game", "ue5game"
        ];

        return !placeholders.Any(placeholder =>
            trimmed.Equals(placeholder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Turns an executable file name into a readable title.
    /// </summary>
    /// <param name="fileName">File name, with or without extension.</param>
    /// <returns>A cleaned-up title, or the original stem when nothing was left after cleaning.</returns>
    internal static string PrettifyFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return "Untitled game";
        }

        // Separators first, so camel-case splitting sees word boundaries.
        var normalised = stem.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');

        var builder = new StringBuilder(normalised.Length + 8);
        for (var i = 0; i < normalised.Length; i++)
        {
            var current = normalised[i];

            // Split only on a lower-to-upper transition. Splitting every capital
            // would shatter acronyms, turning "FTL" into "F T L".
            if (i > 0 &&
                char.IsUpper(current) &&
                (char.IsLower(normalised[i - 1]) || char.IsDigit(normalised[i - 1])))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Strip build tokens from the end only. A game legitimately called
        // "Shipping Wars" must keep its first word.
        while (words.Count > 1 &&
               BuildSuffixTokens.Contains(words[^1], StringComparer.OrdinalIgnoreCase))
        {
            words.RemoveAt(words.Count - 1);
        }

        if (words.Count == 0)
        {
            return stem;
        }

        // Only title-case words that are entirely lower case, so deliberate
        // capitalisation such as "DOOM" or "iRacing" survives.
        var titled = words.Select(word =>
            word.All(char.IsLower)
                ? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(word)
                : word);

        return string.Join(' ', titled);
    }

    /// <summary>Trims a version-resource string, mapping blanks to null.</summary>
    /// <param name="value">Raw value from the version resource.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when it carries nothing.</returns>
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
