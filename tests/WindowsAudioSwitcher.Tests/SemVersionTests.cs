using WindowsAudioSwitcher.Updates;
using Xunit;

namespace WindowsAudioSwitcher.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("0.6.0",                 0, 6, 0, null)]
    [InlineData("v1.2.3",                1, 2, 3, null)]
    [InlineData("V1.2.3",                1, 2, 3, null)]
    [InlineData("0.6.0-rc1",             0, 6, 0, "rc1")]
    [InlineData("1.0.0-beta.2",          1, 0, 0, "beta.2")]
    [InlineData("0.6.0+abc123",          0, 6, 0, null)]   // .NET SDK appends +commit
    [InlineData("0.6.0-rc1+abc123",      0, 6, 0, "rc1")]
    [InlineData("0.6.0.0",               0, 6, 0, null)]   // 4-part assembly version
    public void TryParse_Accepts(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemVersion.TryParse(text, out var v));
        Assert.Equal(new SemVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2")]
    [InlineData("99999999999.0.0")]   // overflows int: clean false, no throw
    public void TryParse_Rejects(string? text)
    {
        Assert.False(SemVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.6.0", "0.6.1")]
    [InlineData("0.6.9", "0.7.0")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("0.6.0-rc1", "0.6.0")]         // a release outranks its pre-releases
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]  // numeric identifiers compare as numbers
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("0.6.10", "0.6.11")]
    public void Ordering_FollowsSemVerPrecedence(string lower, string higher)
    {
        Assert.True(SemVersion.TryParse(lower, out var a));
        Assert.True(SemVersion.TryParse(higher, out var b));
        Assert.True(a < b, $"{lower} should be < {higher}");
        Assert.True(b > a);
        Assert.Equal(-1, Math.Sign(a.CompareTo(b)));
    }

    [Fact]
    public void Equal_VersionsCompareEqual_IgnoringBuildMetadata()
    {
        Assert.True(SemVersion.TryParse("0.6.0+one", out var a));
        Assert.True(SemVersion.TryParse("v0.6.0", out var b));
        Assert.Equal(0, a.CompareTo(b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("0.6.0", "0.6.0")]
    [InlineData("v0.6.0-rc1+sha", "0.6.0-rc1")]
    public void ToString_IsCanonical(string text, string expected)
    {
        Assert.True(SemVersion.TryParse(text, out var v));
        Assert.Equal(expected, v.ToString());
    }

    [Theory]
    [InlineData("0.6.0", "0.6.1", true)]
    [InlineData("0.6.0", "0.6.0", false)]
    [InlineData("0.6.1", "0.6.0", false)]
    [InlineData("0.6.0-rc1", "0.6.0", true)]   // pre-release build is offered the final
    public void UpdateInfo_IsUpdateAvailable(string current, string latest, bool expected)
    {
        SemVersion.TryParse(current, out var c);
        SemVersion.TryParse(latest, out var l);
        var info = new UpdateInfo(c!, l!, "https://github.com/x", null, null, IsPreRelease: false);
        Assert.Equal(expected, info.IsUpdateAvailable);
    }
}
