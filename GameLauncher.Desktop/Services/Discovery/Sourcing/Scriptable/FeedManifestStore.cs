using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Desktop.Infrastructure;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameLauncher.Desktop.Services.Discovery.Sourcing.Scriptable;

/// <summary>
/// Supplies the feed manifests a user has placed in the adapter directory.
/// </summary>
public interface IFeedManifestStore
{
    /// <summary>
    /// Gets the manifests already in hand, without reading the directory.
    /// </summary>
    /// <remarks>
    /// For callers that cannot await — <see cref="ISourcingAdapter.CanHandle"/>
    /// is synchronous by interface. <see langword="null"/> means nothing has
    /// been loaded yet, which is a different answer from "no manifests" and the
    /// caller has to treat it differently.
    /// </remarks>
    IReadOnlyList<FeedManifest>? Cached { get; }

    /// <summary>
    /// Reads every usable manifest.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The manifests, in file-name order.</returns>
    /// <remarks>
    /// Cached until a file in the directory changes, so an install does not
    /// re-read the folder for every candidate address.
    /// </remarks>
    ValueTask<IReadOnlyList<FeedManifest>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards the cache, so the next read goes back to disk.</summary>
    void Invalidate();
}

/// <summary>
/// Default <see cref="IFeedManifestStore"/>, reading
/// <c>*.yaml</c>, <c>*.yml</c> and <c>*.json</c> from the adapter directory.
/// </summary>
/// <remarks>
/// <para>
/// A bad manifest is reported and skipped, never fatal. These files are written
/// by hand, so one with a typo in it is the expected case rather than an
/// exceptional one, and taking the whole sourcing engine down over it would be a
/// poor trade.
/// </para>
/// <para>
/// Freshness is decided by the newest write time in the directory rather than by
/// a file watcher. A watcher would need a lifetime, a synchronisation story and
/// somewhere to put events nobody is listening for; a timestamp comparison on a
/// folder holding a handful of small files costs nothing and cannot leak.
/// </para>
/// </remarks>
public sealed class FeedManifestStore : IFeedManifestStore
{
    private static readonly string[] Extensions = ["*.yaml", "*.yml", "*.json"];

    /// <summary>
    /// Reader settings for JSON manifests.
    /// </summary>
    /// <remarks>
    /// Comments and trailing commas are tolerated because these are files people
    /// edit by hand, and strict JSON punishes that. The enum converter lets
    /// <c>"format": "yaml"</c> read as a name rather than as a number nobody
    /// could be expected to know.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<FeedManifestStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<FeedManifest>? _cached;
    private DateTime _cachedStamp;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="paths">Supplies the adapter directory.</param>
    /// <param name="logger">Logger for manifest diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public FeedManifestStore(IAppPaths paths, ILogger<FeedManifestStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<FeedManifest>? Cached => _cached;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FeedManifest>> GetAsync(CancellationToken cancellationToken = default)
    {
        var stamp = NewestWrite();

        if (_cached is { } cached && stamp == _cachedStamp)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-checked inside the gate: several installs starting at once
            // would otherwise each decide the cache was stale and reload.
            if (_cached is { } current && stamp == _cachedStamp)
            {
                return current;
            }

            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);

            _cached = loaded;
            _cachedStamp = stamp;

            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        _cached = null;
        _cachedStamp = default;
    }

    /// <summary>
    /// Reads and validates every manifest file in the directory.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The usable manifests.</returns>
    private async Task<IReadOnlyList<FeedManifest>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.AdapterDirectory))
        {
            return [];
        }

        var files = Extensions
            .SelectMany(pattern => Directory.EnumerateFiles(_paths.AdapterDirectory, pattern))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var manifests = new List<FeedManifest>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var manifest = await ReadAsync(file, cancellationToken).ConfigureAwait(false);

            if (manifest is null)
            {
                continue;
            }

            if (!manifest.Enabled)
            {
                _logger.LogDebug("Feed manifest '{Key}' is disabled; skipping.", manifest.Key);
                continue;
            }

            // Two manifests claiming one key would make which of them handled an
            // address depend on directory order, so the second is refused and
            // named rather than silently shadowing the first.
            if (!claimed.Add(manifest.Key))
            {
                _logger.LogWarning(
                    "Feed manifest {File} claims key '{Key}', which another manifest already uses; skipping it.",
                    Path.GetFileName(file), manifest.Key);

                continue;
            }

            manifests.Add(manifest);
        }

        if (manifests.Count > 0)
        {
            _logger.LogInformation(
                "Loaded {Count} sourcing feed manifest(s) from {Directory}.",
                manifests.Count, _paths.AdapterDirectory);
        }

        return manifests;
    }

    /// <summary>
    /// Reads one manifest file.
    /// </summary>
    /// <param name="file">Full path to the file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The manifest, or <see langword="null"/> when it cannot be used.</returns>
    private async Task<FeedManifest?> ReadAsync(string file, CancellationToken cancellationToken)
    {
        FeedManifest? manifest;

        try
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);

            manifest = Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Deserialize<FeedManifest>(text, JsonOptions)
                : Yaml().Deserialize<FeedManifest>(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       JsonException or YamlDotNet.Core.YamlException)
        {
            _logger.LogWarning(ex, "Feed manifest {File} could not be read; skipping it.", Path.GetFileName(file));
            return null;
        }

        if (manifest is null)
        {
            _logger.LogWarning("Feed manifest {File} is empty; skipping it.", Path.GetFileName(file));
            return null;
        }

        manifest.SourcePath = file;

        // A file with no key at all is almost certainly a payload sitting beside
        // the manifest that reads it — which is exactly where the local-feed
        // contract puts one. Warning about those every single load would train
        // people to ignore the warnings that matter.
        if (string.IsNullOrWhiteSpace(manifest.Key))
        {
            _logger.LogDebug(
                "{File} names no feed key, so it is not a manifest; ignoring it.", Path.GetFileName(file));

            return null;
        }

        if (manifest.Validate() is { Count: > 0 } problems)
        {
            _logger.LogWarning(
                "Feed manifest {File} is not usable: {Problems}",
                Path.GetFileName(file), string.Join(" ", problems));

            return null;
        }

        return manifest;
    }

    /// <summary>Builds the YAML reader used for manifests.</summary>
    /// <returns>A configured deserializer.</returns>
    /// <remarks>
    /// Unmatched properties are ignored, so a manifest written against a later
    /// version of this contract still loads rather than failing outright. Names
    /// are matched without regard to case for the same reason a hand-written
    /// file deserves it: nobody should lose an evening to <c>filename</c>.
    /// </remarks>
    private static IDeserializer Yaml() =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithCaseInsensitivePropertyMatching()
            .IgnoreUnmatchedProperties()
            .Build();

    /// <summary>
    /// Gets the newest write time in the adapter directory.
    /// </summary>
    /// <returns>The timestamp, or <see cref="DateTime.MinValue"/> when there is nothing there.</returns>
    /// <remarks>
    /// The directory's own timestamp is included, so deleting the last manifest
    /// is noticed as readily as editing one.
    /// </remarks>
    private DateTime NewestWrite()
    {
        try
        {
            if (!Directory.Exists(_paths.AdapterDirectory))
            {
                return DateTime.MinValue;
            }

            var newest = Directory.GetLastWriteTimeUtc(_paths.AdapterDirectory);

            foreach (var pattern in Extensions)
            {
                foreach (var file in Directory.EnumerateFiles(_paths.AdapterDirectory, pattern))
                {
                    var written = File.GetLastWriteTimeUtc(file);

                    if (written > newest)
                    {
                        newest = written;
                    }
                }
            }

            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is treated as unchanged rather than as empty: dropping
            // every manifest because the folder was briefly locked would turn a
            // transient error into a failed install.
            _logger.LogDebug(ex, "Could not stamp the adapter directory; keeping the cached manifests.");
            return _cachedStamp;
        }
    }
}
