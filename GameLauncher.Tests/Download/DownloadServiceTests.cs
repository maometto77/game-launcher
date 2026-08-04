using GameLauncher.Desktop.Services.Download;

namespace GameLauncher.Tests.Download;

/// <summary>
/// Covers the download service's handling of untrusted server input.
/// </summary>
/// <remarks>
/// A <c>Content-Disposition</c> header is chosen by whoever runs the server, not
/// by the user, so the file name derived from it is attacker-controlled for any
/// link the user has not personally vetted.
/// </remarks>
public sealed class DownloadServiceTests
{
    [Theory]
    [InlineData("game.zip", "game.zip")]
    [InlineData("My Game v1.2.zip", "My Game v1.2.zip")]
    public void Ordinary_names_are_preserved(string supplied, string expected)
    {
        Assert.Equal(expected, DownloadService.SanitiseFileName(supplied));
    }

    [Theory]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\Startup\\evil.exe", "evil.exe")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\System32\\bad.dll", "bad.dll")]
    public void Directory_components_are_stripped(string supplied, string expected)
    {
        var result = DownloadService.SanitiseFileName(supplied);

        Assert.Equal(expected, result);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, result);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void Unusable_names_fall_back_to_a_default(string supplied)
    {
        Assert.Equal("download.bin", DownloadService.SanitiseFileName(supplied));
    }

    [Fact]
    public void Invalid_characters_are_replaced()
    {
        var result = DownloadService.SanitiseFileName("game<>:\"|?*.zip");

        Assert.DoesNotContain('<', result);
        Assert.DoesNotContain('?', result);
        Assert.EndsWith(".zip", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Very_long_names_are_truncated()
    {
        var result = DownloadService.SanitiseFileName(new string('a', 400) + ".zip");

        Assert.True(result.Length <= 150);
    }
}
