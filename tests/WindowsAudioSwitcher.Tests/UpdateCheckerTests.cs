using System.Text.Json;
using WindowsAudioSwitcher.Updates;
using Xunit;

namespace WindowsAudioSwitcher.Tests;

public class UpdateCheckerTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly SemVersion Current = new(0, 6, 0);

    private static string Release(
        string tag = "v0.6.1",
        string htmlUrl = "https://github.com/mariosemes/WindowsAudioSwitcher/releases/tag/v0.6.1",
        string? assetUrl = "https://github.com/mariosemes/WindowsAudioSwitcher/releases/download/v0.6.1/WindowsAudioSwitcher-0.6.1-setup.exe",
        string body = "- SHA-256: `" + Sha + "`\n- Size: 70 MB",
        bool draft = false,
        bool prerelease = false)
    {
        var assets = assetUrl == null
            ? Array.Empty<object>()
            : new object[] { new { name = "WindowsAudioSwitcher-0.6.1-setup.exe", browser_download_url = assetUrl, size = 73400320L } };
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = htmlUrl,
            draft,
            prerelease,
            body,
            assets,
        });
    }

    [Fact]
    public void ParseRelease_HappyPath()
    {
        var info = UpdateChecker.ParseRelease(Release(), Current);
        Assert.NotNull(info);
        Assert.Equal(new SemVersion(0, 6, 1), info!.LatestVersion);
        Assert.True(info.IsUpdateAvailable);
        Assert.False(info.IsPreRelease);
        Assert.EndsWith("-setup.exe", info.InstallerUrl);
        Assert.Equal(73400320L, info.InstallerSizeBytes);
        Assert.Equal(Sha, info.ExpectedSha256);
    }

    [Fact]
    public void ParseRelease_Draft_ReturnsNull()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release(draft: true), Current));
    }

    [Fact]
    public void ParseRelease_UnparsableTag_ReturnsNull()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release(tag: "latest"), Current));
    }

    [Fact]
    public void ParseRelease_PreReleaseFlagOrTag_MarkedPreRelease()
    {
        Assert.True(UpdateChecker.ParseRelease(Release(prerelease: true), Current)!.IsPreRelease);
        Assert.True(UpdateChecker.ParseRelease(Release(tag: "v0.6.1-rc1"), Current)!.IsPreRelease);
    }

    [Theory]
    [InlineData("https://evil.example/mariosemes/WindowsAudioSwitcher/releases/tag/v0.6.1")]
    [InlineData("http://github.com/mariosemes/WindowsAudioSwitcher/releases/tag/v0.6.1")]
    [InlineData("https://github.com/someoneelse/WindowsAudioSwitcher/releases/tag/v0.6.1")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    public void ParseRelease_UntrustedReleasePage_ReturnsNull(string htmlUrl)
    {
        Assert.Null(UpdateChecker.ParseRelease(Release(htmlUrl: htmlUrl), Current));
    }

    [Theory]
    [InlineData("https://evil.example/WindowsAudioSwitcher-0.6.1-setup.exe")]
    [InlineData("http://github.com/mariosemes/WindowsAudioSwitcher/releases/download/v0.6.1/x-setup.exe")]
    [InlineData("https://github.com/mariosemes/WindowsAudioSwitcher/releases/download/../../../evil/x-setup.exe")]
    public void ParseRelease_UntrustedInstallerUrl_DroppedSoUiFallsBackToReleasePage(string assetUrl)
    {
        var info = UpdateChecker.ParseRelease(Release(assetUrl: assetUrl), Current);
        Assert.NotNull(info);
        Assert.Null(info!.InstallerUrl);
        Assert.Null(info.InstallerSizeBytes);
    }

    [Fact]
    public void ParseRelease_WrongTypesInJson_DoesNotThrow()
    {
        const string json = """{ "tag_name": "v0.6.1", "html_url": "https://github.com/mariosemes/WindowsAudioSwitcher/releases/tag/v0.6.1", "draft": null, "prerelease": "no", "assets": [ { "name": "a-setup.exe", "size": "big" } ], "body": 5 }""";
        var info = UpdateChecker.ParseRelease(json, Current);
        Assert.NotNull(info);
        Assert.Null(info!.InstallerUrl);
        Assert.Null(info.ExpectedSha256);
    }

    [Theory]
    [InlineData("- SHA-256: `" + Sha + "`", Sha)]
    [InlineData("SHA256 " + Sha, Sha)]
    [InlineData("sha-256:\t" + Sha + "\n", Sha)]
    [InlineData("no hash here", null)]
    [InlineData("SHA-256: " + Sha + "ff", null)]   // longer hex run is not a SHA-256
    public void ExtractSha256(string body, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.ExtractSha256(body));
    }

    [Theory]
    [InlineData("https://github.com/mariosemes/WindowsAudioSwitcher/releases/download/v0.6.1/a-setup.exe", true)]
    [InlineData("https://GITHUB.com/mariosemes/windowsaudioswitcher/releases/download/v0.6.1/a-setup.exe", true)]
    [InlineData("https://github.com:8443/mariosemes/WindowsAudioSwitcher/releases/download/v0.6.1/a-setup.exe", false)]
    [InlineData("https://user@github.com/mariosemes/WindowsAudioSwitcher/releases/download/v0.6.1/a-setup.exe", false)]
    [InlineData("https://github.com.evil.example/mariosemes/WindowsAudioSwitcher/releases/download/v0.6.1/a-setup.exe", false)]
    [InlineData("https://github.com/mariosemes/WindowsAudioSwitcher/archive/main.zip", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void UpdateUrls_IsTrustedDownload(string? url, bool expected)
    {
        Assert.Equal(expected, UpdateUrls.IsTrustedDownload(url));
    }
}
