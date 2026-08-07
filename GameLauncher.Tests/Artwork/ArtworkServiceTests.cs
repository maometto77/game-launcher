using GameLauncher.Desktop.Infrastructure;
using GameLauncher.Desktop.Models;
using GameLauncher.Desktop.Services.Artwork;
using GameLauncher.Desktop.Services.Database;
using GameLauncher.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Tests.Artwork;

/// <summary>
/// Covers finding, downloading and applying game artwork.
/// </summary>
/// <remarks>
/// The provider is substituted rather than the HTTP transport, because the
/// interesting behaviour lives in the service: choosing a candidate, naming the
/// file from the game's own identity rather than the remote URL, replacing rather
/// than accumulating, and never letting one missing image kind block the other.
/// The provider's own HTTP handling is covered separately.
/// </remarks>
public sealed class ArtworkServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Artwork_is_downloaded_and_recorded_against_the_game()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("cover.png", PngBytes(1));
        server.AddFile("hero.png", PngBytes(2));

        using var host = new TestAppHost(_root);
        var game = await SeedAsync(host, "Kung Fu Panda");

        var provider = new StubProvider(server)
        {
            Matches = [new ArtworkGameMatch(42, "Kung Fu Panda")],
            Covers = ["cover.png"],
            Heroes = ["hero.png"]
        };

        var service = Build(host, provider);
        var result = await service.ApplyArtworkAsync(game);

        Assert.True(result.FoundAnything);
        Assert.Equal("Kung Fu Panda", result.MatchedName);

        Assert.NotNull(game.CoverArtPath);
        Assert.NotNull(game.HeroArtPath);
        Assert.True(File.Exists(game.CoverArtPath!));
        Assert.True(File.Exists(game.HeroArtPath!));

        // Persisted, not merely set in memory: reopening the library must show it.
        var reloaded = await host.Resolve<IGameRepository>().GetByIdAsync(game.Id);
        Assert.Equal(game.CoverArtPath, reloaded!.CoverArtPath);
        Assert.Equal(game.HeroArtPath, reloaded.HeroArtPath);

        // No half-written temporary left behind.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artwork"), "*.part"));
    }

    [Fact]
    public async Task The_file_name_comes_from_the_game_not_the_remote_url()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        // A server-chosen name that would escape the artwork folder if it were
        // ever used to build the path.
        server.AddFile("..%2F..%2Fevil.png", PngBytes(3));
        server.AddFile("cover.png", PngBytes(3));

        using var host = new TestAppHost(_root);
        var game = await SeedAsync(host, "Kung Fu Panda");

        var provider = new StubProvider(server)
        {
            Matches = [new ArtworkGameMatch(42, "Kung Fu Panda")],
            Covers = ["cover.png"]
        };

        var service = Build(host, provider);
        await service.ApplyArtworkAsync(game);

        var artworkFolder = Path.GetFullPath(Path.Combine(_root, "artwork"));

        Assert.StartsWith(artworkFolder, Path.GetFullPath(game.CoverArtPath!), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(game.GlobalKey, Path.GetFileName(game.CoverArtPath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refetching_replaces_the_image_rather_than_accumulating_copies()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("cover.png", PngBytes(1));

        using var host = new TestAppHost(_root);
        var game = await SeedAsync(host, "Kung Fu Panda");

        var provider = new StubProvider(server)
        {
            Matches = [new ArtworkGameMatch(42, "Kung Fu Panda")],
            Covers = ["cover.png"]
        };

        var service = Build(host, provider);

        await service.ApplyArtworkAsync(game);
        var first = game.CoverArtPath;

        server.AddFile("cover.png", PngBytes(9));
        await service.ApplyArtworkAsync(game);

        // Same stable path, new contents. A URL-derived name would leave the old
        // file behind and grow the folder every time.
        Assert.Equal(first, game.CoverArtPath);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "artwork"), "*-cover.*"));
        Assert.Equal(PngBytes(9), await File.ReadAllBytesAsync(game.CoverArtPath!));
    }

    [Fact]
    public async Task A_game_with_only_a_cover_still_gets_the_cover()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("cover.png", PngBytes(1));

        using var host = new TestAppHost(_root);
        var game = await SeedAsync(host, "Obscure Title");

        var provider = new StubProvider(server)
        {
            Matches = [new ArtworkGameMatch(7, "Obscure Title")],
            Covers = ["cover.png"],
            Heroes = []
        };

        var result = await Build(host, provider).ApplyArtworkAsync(game);

        // One kind missing must not deny the other.
        Assert.True(result.FoundAnything);
        Assert.NotNull(game.CoverArtPath);
        Assert.Null(game.HeroArtPath);
        Assert.Contains("cover only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_title_nobody_recognises_reports_that_and_changes_nothing()
    {
        await using var server = await LoopbackFileServer.StartAsync();

        using var host = new TestAppHost(_root);
        var game = await SeedAsync(host, "Not A Real Game");

        var result = await Build(host, new StubProvider(server) { Matches = [] })
            .ApplyArtworkAsync(game);

        Assert.False(result.FoundAnything);
        Assert.Null(game.CoverArtPath);
        Assert.Null(game.HeroArtPath);
        Assert.Contains("Not A Real Game", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_search_title_overrides_the_game_title()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        server.AddFile("cover.png", PngBytes(1));

        using var host = new TestAppHost(_root);

        // The kind of name a repack folder produces.
        var game = await SeedAsync(host, "KungFuPanda-RELOADED");

        var provider = new StubProvider(server)
        {
            Matches = [new ArtworkGameMatch(42, "Kung Fu Panda")],
            Covers = ["cover.png"]
        };

        await Build(host, provider).ApplyArtworkAsync(game, "Kung Fu Panda");

        Assert.Equal("Kung Fu Panda", provider.LastSearchedTitle);
    }

    [Fact]
    public async Task An_unconfigured_provider_reports_itself_as_unavailable()
    {
        await using var server = await LoopbackFileServer.StartAsync();
        using var host = new TestAppHost(_root);

        var service = Build(host, new StubProvider(server) { IsConfigured = false });

        Assert.False(service.IsConfigured);
    }

    /// <summary>Builds the service under test around a substituted provider.</summary>
    /// <param name="host">Container supplying the repository and paths.</param>
    /// <param name="provider">The provider to use.</param>
    /// <returns>The service.</returns>
    private static IArtworkService Build(TestAppHost host, IArtworkProvider provider) =>
        new ArtworkService(
            provider,
            host.Resolve<IGameRepository>(),
            host.Resolve<System.Net.Http.IHttpClientFactory>(),
            host.Resolve<IAppPaths>(),
            NullLogger<ArtworkService>.Instance);

    /// <summary>Adds a game to the library.</summary>
    /// <param name="host">Container supplying the repository.</param>
    /// <param name="title">Title to give it.</param>
    /// <returns>The stored game.</returns>
    private static async Task<Game> SeedAsync(TestAppHost host, string title)
    {
        var game = new Game
        {
            Title = title,
            ExecutablePath = @"C:\Games\Sample\game.exe",
            DateAdded = DateTimeOffset.Now,
            Tags = []
        };

        await host.Resolve<IGameRepository>().AddAsync(game);
        return game;
    }

    /// <summary>Builds distinguishable bytes carrying a PNG signature.</summary>
    /// <param name="seed">Value written into the payload so images can be told apart.</param>
    /// <returns>The bytes.</returns>
    private static byte[] PngBytes(byte seed)
    {
        var content = new byte[64];
        Array.Fill(content, seed);

        // Only the signature matters; nothing here decodes the image.
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(content, 0);

        return content;
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort.
        }
    }

    /// <summary>A provider serving candidates from the loopback server.</summary>
    private sealed class StubProvider : IArtworkProvider
    {
        private readonly LoopbackFileServer _server;

        public StubProvider(LoopbackFileServer server) => _server = server;

        public IReadOnlyList<ArtworkGameMatch> Matches { get; init; } = [];

        public IReadOnlyList<string> Covers { get; init; } = [];

        public IReadOnlyList<string> Heroes { get; init; } = [];

        public string? LastSearchedTitle { get; private set; }

        public string DisplayName => "Stub";

        public bool IsConfigured { get; init; } = true;

        public Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(
            string title,
            CancellationToken cancellationToken = default)
        {
            LastSearchedTitle = title;
            return Task.FromResult(Matches);
        }

        public Task<IReadOnlyList<ArtworkCandidate>> GetCandidatesAsync(
            int providerGameId,
            ArtworkKind kind,
            CancellationToken cancellationToken = default)
        {
            var names = kind == ArtworkKind.Hero ? Heroes : Covers;

            IReadOnlyList<ArtworkCandidate> candidates = names
                .Select(name => new ArtworkCandidate(kind, _server.FileUrl(name), 600, 900, 100))
                .ToArray();

            return Task.FromResult(candidates);
        }
    }
}
