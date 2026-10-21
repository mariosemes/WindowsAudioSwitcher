using WindowsAudioSwitcher.Rules;
using Xunit;

namespace WindowsAudioSwitcher.Tests;

public class RuleTests
{
    [Theory]
    [InlineData("Sony WH-1000XM4 Stereo", "sony",     true)]
    [InlineData("Sony WH-1000XM4 Stereo", "SONY",     true)]
    [InlineData("Sony WH-1000XM4 Stereo", "WH-1000",  true)]
    [InlineData("Realtek(R) Audio",       "sony",     false)]
    [InlineData("Anything",               "",         false)] // empty needle never matches
    [InlineData("Anything",               "   ",      false)] // whitespace-only needle never matches
    public void NameContains_IsCaseInsensitive(string friendly, string needle, bool expected)
    {
        var rule = new Rule { Kind = RuleKind.NameContains, Value = needle };
        Assert.Equal(expected, rule.Matches(deviceId: "irrelevant", friendlyName: friendly));
    }

    private const string DeviceA = "{0.0.0.00000000}.{53337f7b-13f2-4ee0-8b4a-3dc570207574}";

    [Theory]
    [InlineData("{0.0.0.00000000}.{53337f7b-13f2-4ee0-8b4a-3dc570207574}", true)]
    [InlineData("{0.0.0.00000000}.{53337F7B-13F2-4EE0-8B4A-3DC570207574}", true)] // case-insensitive
    [InlineData("{0.0.0.00000000}.{other-guid}",                          false)]
    public void ExactDevice_MatchesByIdCaseInsensitively(string actualDeviceId, bool expected)
    {
        var rule = new Rule { Kind = RuleKind.ExactDevice, Value = DeviceA };
        Assert.Equal(expected, rule.Matches(actualDeviceId, friendlyName: "any name"));
    }

    [Fact]
    public void Matches_DoesNotConsultIsEnabled()
    {
        // IsEnabled is enforced by RuleEngine, not Rule.Matches itself.
        var rule = new Rule { Kind = RuleKind.NameContains, Value = "Sony", IsEnabled = false };
        Assert.True(rule.Matches("id", "Sony Headphones"));
    }

    [Fact]
    public void DisplayText_NameContains_ShowsQuotedValue()
    {
        var rule = new Rule { Kind = RuleKind.NameContains, Value = "Sony" };
        Assert.Equal("Name contains: \"Sony\"", rule.DisplayText);
    }

    [Fact]
    public void DisplayText_ExactDevice_PrefersLabelOverValue()
    {
        var rule = new Rule { Kind = RuleKind.ExactDevice, Value = "{abc}", Label = "Sony WH-1000XM4" };
        Assert.Equal("Sony WH-1000XM4", rule.DisplayText);
    }

    [Fact]
    public void DisplayText_ExactDevice_FallsBackToValueWhenNoLabel()
    {
        var rule = new Rule { Kind = RuleKind.ExactDevice, Value = "{abc}" };
        Assert.Equal("{abc}", rule.DisplayText);
    }

    [Fact]
    public void IsEnabled_RaisesPropertyChanged()
    {
        var rule = new Rule { IsEnabled = true };
        var fired = new List<string?>();
        rule.PropertyChanged += (_, e) => fired.Add(e.PropertyName);
        rule.IsEnabled = false;
        Assert.Contains(nameof(Rule.IsEnabled), fired);
        Assert.Contains(nameof(Rule.StatusText), fired);
    }

    [Fact]
    public void SetStatus_RaisesChangeOnlyWhenSomethingChanged()
    {
        var rule = new Rule();
        var fired = new List<string?>();
        rule.PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        rule.SetStatus(RuleStatus.Active, "Sony");
        rule.SetStatus(RuleStatus.Active, "Sony");

        Assert.Equal(new[] { nameof(Rule.Status), nameof(Rule.StatusText) }, fired);
        Assert.Equal("In use: Sony", rule.StatusText);
    }

    [Fact]
    public void StatusText_ConnectedButDisabled_SaysRuleIsOff()
    {
        var rule = new Rule { IsEnabled = false };
        rule.SetStatus(RuleStatus.Connected, "Sony");
        Assert.Contains("turned off", rule.StatusText);
    }

    [Fact]
    public void Clone_CopiesPersistedFieldsIntoIndependentInstance()
    {
        var rule = new Rule { Kind = RuleKind.NameContains, Value = "Sony", Label = "L", IsEnabled = false };
        var copy = rule.Clone();
        Assert.NotSame(rule, copy);
        Assert.Equal((rule.Kind, rule.Value, rule.Label, rule.IsEnabled), (copy.Kind, copy.Value, copy.Label, copy.IsEnabled));

        rule.IsEnabled = true;
        Assert.False(copy.IsEnabled);
    }
}
