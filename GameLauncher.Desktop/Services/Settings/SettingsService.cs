using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Settings;

/// <summary>
/// Default <see cref="ISettingsService"/>, backed by a JSON file.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly IIdentityGenerator _identity;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="paths">Supplies the settings file location.</param>
    /// <param name="identity">Generates a first-run friend code and display name.</param>
    /// <param name="dispatcher">Used to raise change notifications on the UI thread.</param>
    /// <param name="logger">Logger for settings diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SettingsService(
        IAppPaths paths,
        IIdentityGenerator identity,
        IUiDispatcher dispatcher,
        ILogger<SettingsService> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Current = new AppSettings();
    }

    /// <inheritdoc />
    public AppSettings Current { get; private set; }

    /// <inheritdoc />
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var settings = MigrateLegacyIdentity(await ReadAsync(cancellationToken).ConfigureAwait(false));

            // First run, or a file that predates identity: mint one now so the
            // friend code is stable from the very first launch rather than being
            // generated later and appearing to change.
            var needsIdentity =
                !FriendCodeContract.IsValid(settings.FriendCode) ||
                string.IsNullOrWhiteSpace(settings.DisplayName);

            if (needsIdentity)
            {
                settings = settings with
                {
                    FriendCode = FriendCodeContract.IsValid(settings.FriendCode)
                        ? settings.FriendCode
                        : _identity.NewFriendCode(),
                    DisplayName = string.IsNullOrWhiteSpace(settings.DisplayName)
                        ? _identity.SuggestDisplayName()
                        : settings.DisplayName
                };

                await WriteAsync(settings, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Generated a friend code for this installation.");
            }

            Current = settings;
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteAsync(settings, cancellationToken).ConfigureAwait(false);
            Current = settings;
        }
        finally
        {
            _gate.Release();
        }

        await _dispatcher
            .InvokeAsync(() => SettingsChanged?.Invoke(this, settings))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Moves credentials from the older flat fields into the per-relay list.
    /// </summary>
    /// <param name="settings">Settings as read from disk.</param>
    /// <returns>Settings with any legacy credentials migrated.</returns>
    /// <remarks>
    /// <para>
    /// Earlier builds stored a single token, on the assumption that an
    /// installation talks to exactly one relay forever. Discarding it on upgrade
    /// would silently deregister the user, so it is carried across instead.
    /// </para>
    /// <para>
    /// The relay identity is left null because a settings file cannot tell us
    /// which relay issued the token — relays did not report an identity when it
    /// was written. It is bound to a real identity by the first successful
    /// <c>/relay-info</c> call against the matching address.
    /// </para>
    /// <para>
    /// Idempotent: once the legacy fields are cleared there is nothing left to
    /// migrate.
    /// </para>
    /// </remarks>
    private static AppSettings MigrateLegacyIdentity(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.RelayAuthToken))
        {
            return settings;
        }

        var carried = new RelayIdentity
        {
            RelayId = null,
            RelayUrl = settings.RelayUrl,
            FriendCode = settings.FriendCode,
            AuthToken = settings.RelayAuthToken,
            DeviceId = settings.RelayDeviceId,
            LastUsedAt = DateTimeOffset.Now
        };

        return settings with
        {
            SchemaVersion = 2,
            RelayIdentities = [.. settings.RelayIdentities, carried],
            RelayAuthToken = null,
            RelayDeviceId = null
        };
    }

    /// <summary>Reads the settings file, falling back to defaults.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored settings, or defaults when none could be read.</returns>
    private async Task<AppSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var path = _paths.SettingsFile;

        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(path);

            return await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unreadable settings must not stop the app starting. The
            // defaults are serviceable, and the file is rewritten on next save.
            _logger.LogWarning(ex, "Could not read settings from {Path}; using defaults.", path);
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings file atomically.</summary>
    /// <param name="settings">Settings to persist.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// Written to a temporary file and moved into place, so that losing power
    /// mid-write leaves the previous settings intact rather than a truncated file
    /// that would take the friend code with it.
    /// </remarks>
    private async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var path = _paths.SettingsFile;
        var temporary = path + ".tmp";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer
                .SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);

        // Stated with what the file system says afterwards, not merely that the
        // call returned. "Saved" on its own is a claim about this method having
        // run; the length and write time are a claim about the file, and the two
        // coming apart is the only signal that something outside this process is
        // putting the file back. Diagnosing that without these numbers meant
        // comparing a log that said saved against a file dated days earlier, and
        // being unable to tell which was lying.
        try
        {
            var written = new FileInfo(path);

            _logger.LogDebug(
                "Settings saved to {Path} ({Bytes} bytes, written {Written:O}).",
                path, written.Length, written.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write succeeded; only the confirmation failed. Reporting that
            // as a failed save would be worse than reporting nothing.
            _logger.LogDebug(ex, "Settings saved to {Path}, but it could not be re-read to confirm.", path);
        }
    }
}
