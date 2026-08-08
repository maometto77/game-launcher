using System.Net.Http;
using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Services.Discovery.Normalization;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameLauncher.Desktop.Services.Saves;

/// <summary>
/// Default <see cref="ISavePathResolver"/>, backed by the Ludusavi manifest.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is a single YAML document of well over ten megabytes covering
/// thousands of games. It is downloaded once, cached on disk, and refreshed when
/// the cached copy goes stale.
/// </para>
/// <para>
/// It is deliberately <em>not</em> held in memory as parsed. Two reductions are
/// applied while reading it: entries whose conditions exclude this operating
/// system are dropped, and so are locations tagged only <c>config</c>. What
/// survives is a fraction of the file and is what a save feature actually wants
/// — which is the difference between an index worth keeping resident and one
/// that is not.
/// </para>
/// </remarks>
public sealed class LudusaviSavePathResolver : ISavePathResolver
{
    /// <summary>Name of the configured <see cref="HttpClient"/> used to fetch the manifest.</summary>
    public const string HttpClientName = "ludusavi";

    private const string ManifestUrl =
        "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";

    /// <summary>How long a cached manifest is trusted before being fetched again.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(14);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppPaths _paths;
    private readonly ILogger<LudusaviSavePathResolver> _logger;

    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private ManifestIndex? _index;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the client used to fetch the manifest.</param>
    /// <param name="paths">Resolves where the manifest is cached.</param>
    /// <param name="logger">Logger for resolver diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public LudusaviSavePathResolver(
        IHttpClientFactory httpClientFactory,
        IAppPaths paths,
        ILogger<LudusaviSavePathResolver> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets where the manifest is cached.</summary>
    private string CachePath => Path.Combine(_paths.RootDirectory, "ludusavi-manifest.yaml");

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync(cancellationToken).ConfigureAwait(false) is { Games.Count: > 0 };

    /// <inheritdoc />
    public async Task<SavePathResult> ResolveAsync(
        SavePathQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var index = await LoadAsync(cancellationToken).ConfigureAwait(false);

        if (index is null)
        {
            return SavePathResult.NotFound;
        }

        var entry = Find(index, query);

        if (entry is null)
        {
            return SavePathResult.NotFound;
        }

        var locations = new List<SaveLocation>();

        foreach (var candidate in entry.Locations)
        {
            var expanded = candidate.Kind == SaveLocationKind.Registry
                ? candidate.Template
                : LudusaviPathExpander.Expand(
                    candidate.Template,
                    query.InstallDirectory,
                    entry.SteamAppId?.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (expanded is null)
            {
                // A path this machine cannot resolve — usually one relative to an
                // install directory the caller did not supply.
                continue;
            }

            var exists = candidate.Kind switch
            {
                SaveLocationKind.Registry => true,
                _ => File.Exists(expanded) || Directory.Exists(expanded)
            };

            if (!exists && !query.IncludeMissing)
            {
                continue;
            }

            var kind = candidate.Kind == SaveLocationKind.Registry
                ? SaveLocationKind.Registry
                : Directory.Exists(expanded) ? SaveLocationKind.Directory : SaveLocationKind.File;

            locations.Add(new SaveLocation(expanded, kind, candidate.Tags, exists));
        }

        // Save-tagged first: a caller taking only the first location should get
        // the saves, not a settings file that happened to sort earlier.
        var ordered = locations
            .OrderByDescending(location => location.Tags.Contains("save", StringComparer.OrdinalIgnoreCase))
            .ThenByDescending(location => location.Exists)
            .ToArray();

        return new SavePathResult(entry.Title, ordered);
    }

    /// <inheritdoc />
    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!await DownloadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            _index = Parse(CachePath);

            return _index?.Games.Count ?? 0;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Loads the index, downloading or re-reading the manifest when needed.
    /// </summary>
    /// <param name="cancellationToken">Cancels any fetch.</param>
    /// <returns>The index, or <see langword="null"/> when no manifest could be obtained.</returns>
    private async Task<ManifestIndex?> LoadAsync(CancellationToken cancellationToken)
    {
        if (_index is not null)
        {
            return _index;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_index is not null)
            {
                return _index;
            }

            var stale = !File.Exists(CachePath) ||
                        DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(CachePath) > CacheLifetime;

            if (stale && !await DownloadAsync(cancellationToken).ConfigureAwait(false) &&
                !File.Exists(CachePath))
            {
                // The download failed and there is nothing cached. Not an error:
                // the answer is simply that nothing is known about any game.
                return null;
            }

            _index = Parse(CachePath);

            return _index;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Downloads the manifest into the cache.
    /// </summary>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns><see langword="true"/> when a fresh copy was written.</returns>
    /// <remarks>
    /// Written to a temporary file and moved into place, so a failed download
    /// never replaces a good cached manifest with a truncated one.
    /// </remarks>
    private async Task<bool> DownloadAsync(CancellationToken cancellationToken)
    {
        var temporary = CachePath + ".part";

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var response = await client
                .GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            Directory.CreateDirectory(_paths.RootDirectory);

            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                             temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             64 * 1024, useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, CachePath, overwrite: true);

            _logger.LogInformation("Downloaded the Ludusavi save manifest.");

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not download the Ludusavi save manifest.");

            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A leftover part file is untidy, never incorrect.
            }

            return false;
        }
    }

    /// <summary>
    /// Reads the manifest into a compact index.
    /// </summary>
    /// <param name="path">The cached manifest.</param>
    /// <returns>The index, or <see langword="null"/> when the file cannot be read.</returns>
    /// <remarks>
    /// <para>
    /// Deserialised into deliberately minimal types — only the three keys this
    /// needs, with unmatched properties ignored — so the parser walks past
    /// <c>launch</c>, <c>installDir</c> and the rest without building objects
    /// for them.
    /// </para>
    /// <para>
    /// Measured against the real manifest: sixteen megabytes indexes in about
    /// four seconds and leaves roughly twenty megabytes resident. That cost is
    /// paid lazily on the first lookup rather than at startup, which is why
    /// <see cref="LoadAsync"/> is only called from a query.
    /// </para>
    /// </remarks>
    private ManifestIndex? Parse(string path)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            using var reader = new StreamReader(path);

            var document = deserializer.Deserialize<Dictionary<string, ManifestGame>>(reader);

            if (document is null || document.Count == 0)
            {
                return null;
            }

            var games = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            var byTitle = new Dictionary<string, string>(StringComparer.Ordinal);
            var bySteam = new Dictionary<int, string>();

            foreach (var (title, game) in document)
            {
                if (string.IsNullOrWhiteSpace(title) || game is null)
                {
                    continue;
                }

                var entry = ReadEntry(title, game);

                if (entry is null)
                {
                    continue;
                }

                games[title] = entry;

                // The same normalised title can legitimately appear twice —
                // regional variants, re-releases. First wins rather than last,
                // so the index does not depend on document order.
                byTitle.TryAdd(TitleNormalizer.Normalize(title), title);

                if (entry.SteamAppId is { } appId)
                {
                    bySteam.TryAdd(appId, title);
                }
            }

            _logger.LogInformation(
                "Indexed {Games} games with save locations from the Ludusavi manifest.", games.Count);

            return new ManifestIndex(games, byTitle, bySteam);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The cached Ludusavi manifest could not be read.");
            return null;
        }
    }

    /// <summary>
    /// Reads one game, keeping only what is worth keeping.
    /// </summary>
    /// <param name="title">The game's name in the manifest.</param>
    /// <param name="game">Its deserialised entry.</param>
    /// <returns>The entry, or <see langword="null"/> when it has nothing useful.</returns>
    private static ManifestEntry? ReadEntry(string title, ManifestGame game)
    {
        var locations = new List<ManifestLocation>();

        foreach (var (template, spec) in game.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(template) || !AppliesToThisOs(spec))
            {
                continue;
            }

            var tags = ReadTags(spec);

            // Config-only entries are dropped. Keeping them would roughly double
            // the index to describe files a save feature does not want.
            if (!tags.Contains("save", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            locations.Add(new ManifestLocation(template, SaveLocationKind.File, tags));
        }

        // Registry entries only mean anything on Windows.
        if (OperatingSystem.IsWindows())
        {
            foreach (var (key, spec) in game.Registry ?? [])
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var tags = ReadTags(spec);

                if (!tags.Contains("save", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                locations.Add(new ManifestLocation(key, SaveLocationKind.Registry, tags));
            }
        }

        var steamId = game.Steam?.Id;

        return locations.Count == 0 && steamId is null
            ? null
            : new ManifestEntry(title, locations, steamId);
    }

    /// <summary>
    /// Decides whether an entry's conditions include this operating system.
    /// </summary>
    /// <param name="spec">The entry's specification.</param>
    /// <returns><see langword="true"/> when it applies here.</returns>
    /// <remarks>
    /// An entry with no <c>when</c> applies everywhere. One that names operating
    /// systems applies only if this is among them; a condition naming only a
    /// store constrains where the game came from, not which platform it runs on,
    /// and is treated as applicable.
    /// </remarks>
    private static bool AppliesToThisOs(ManifestFileSpec? spec)
    {
        if (spec?.When is not { Count: > 0 } conditions)
        {
            return true;
        }

        var sawOsCondition = false;

        foreach (var condition in conditions)
        {
            if (string.IsNullOrWhiteSpace(condition?.Os))
            {
                // Store-only condition: says nothing about the platform.
                return true;
            }

            sawOsCondition = true;

            if (IsCurrentOs(condition.Os))
            {
                return true;
            }
        }

        return !sawOsCondition;
    }

    /// <summary>Determines whether a manifest OS name is the one running.</summary>
    /// <param name="os">The manifest's name for an operating system.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    private static bool IsCurrentOs(string os) => os.ToLowerInvariant() switch
    {
        "windows" => OperatingSystem.IsWindows(),
        "linux" => OperatingSystem.IsLinux(),
        "mac" or "macos" => OperatingSystem.IsMacOS(),
        _ => false
    };

    /// <summary>Reads an entry's tags.</summary>
    /// <param name="spec">The entry's specification.</param>
    /// <returns>The tags, or <c>save</c> when none are stated.</returns>
    /// <remarks>
    /// An untagged entry is treated as a save. That is what the manifest means by
    /// omitting tags, and assuming otherwise would silently drop entries.
    /// </remarks>
    private static IReadOnlyList<string> ReadTags(ManifestFileSpec? spec) =>
        spec?.Tags is { Count: > 0 } tags
            ? tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray()
            : ["save"];

    /// <summary>
    /// Finds the manifest entry for a query.
    /// </summary>
    /// <param name="index">The parsed index.</param>
    /// <param name="query">What was asked about.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Steam id first when it is known: it identifies a game exactly, whereas a
    /// title has to be matched and can be matched wrongly.
    /// </remarks>
    private static ManifestEntry? Find(ManifestIndex index, SavePathQuery query)
    {
        if (query.SteamAppId is { } appId &&
            index.BySteamAppId.TryGetValue(appId, out var bySteam) &&
            index.Games.TryGetValue(bySteam, out var steamEntry))
        {
            return steamEntry;
        }

        if (string.IsNullOrWhiteSpace(query.Title))
        {
            return null;
        }

        if (index.Games.TryGetValue(query.Title, out var exact))
        {
            return exact;
        }

        // The same normalisation the discovery catalogue matches titles with, so
        // "Oregon Trail, The" and "The Oregon Trail" reach the same entry.
        var normalised = TitleNormalizer.Normalize(query.Title);

        return index.ByNormalizedTitle.TryGetValue(normalised, out var byTitle) &&
               index.Games.TryGetValue(byTitle, out var titleEntry)
            ? titleEntry
            : null;
    }

    /// <summary>The parsed manifest, reduced to what is worth keeping resident.</summary>
    /// <param name="Games">Entries by their manifest title.</param>
    /// <param name="ByNormalizedTitle">Normalised title to manifest title.</param>
    /// <param name="BySteamAppId">Steam application id to manifest title.</param>
    private sealed record ManifestIndex(
        IReadOnlyDictionary<string, ManifestEntry> Games,
        IReadOnlyDictionary<string, string> ByNormalizedTitle,
        IReadOnlyDictionary<int, string> BySteamAppId);

    /// <summary>One game's save locations.</summary>
    /// <param name="Title">The game's name in the manifest.</param>
    /// <param name="Locations">Where it keeps things worth preserving.</param>
    /// <param name="SteamAppId">Its Steam application id, when stated.</param>
    private sealed record ManifestEntry(
        string Title,
        IReadOnlyList<ManifestLocation> Locations,
        int? SteamAppId);

    /// <summary>One unexpanded location.</summary>
    /// <param name="Template">The path or registry key as the manifest writes it.</param>
    /// <param name="Kind">What it refers to.</param>
    /// <param name="Tags">What it holds.</param>
    private sealed record ManifestLocation(
        string Template,
        SaveLocationKind Kind,
        IReadOnlyList<string> Tags);

    /// <summary>
    /// One game as the manifest declares it.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal. The manifest also carries <c>installDir</c>,
    /// <c>launch</c>, <c>gog</c> and more; declaring only what is used lets the
    /// deserialiser walk past the rest without allocating for it, which is the
    /// difference between parsing this file in seconds and in minutes.
    /// </remarks>
    private sealed class ManifestGame
    {
        /// <summary>Paths the game reads and writes, keyed by path template.</summary>
        public Dictionary<string, ManifestFileSpec?>? Files { get; set; }

        /// <summary>Registry keys the game uses, keyed by key path.</summary>
        public Dictionary<string, ManifestFileSpec?>? Registry { get; set; }

        /// <summary>Steam identification, when the game is on Steam.</summary>
        public ManifestStore? Steam { get; set; }
    }

    /// <summary>What one path or registry key holds, and when it applies.</summary>
    private sealed class ManifestFileSpec
    {
        /// <summary>What the location holds — <c>save</c>, <c>config</c>.</summary>
        public List<string>? Tags { get; set; }

        /// <summary>Conditions narrowing where the location applies.</summary>
        public List<ManifestCondition?>? When { get; set; }
    }

    /// <summary>One condition on a location.</summary>
    private sealed class ManifestCondition
    {
        /// <summary>Operating system the location applies to.</summary>
        public string? Os { get; set; }

        /// <summary>Store the game must have come from.</summary>
        public string? Store { get; set; }
    }

    /// <summary>A store's identifier for the game.</summary>
    private sealed class ManifestStore
    {
        /// <summary>The store's numeric id.</summary>
        public int? Id { get; set; }
    }
}
