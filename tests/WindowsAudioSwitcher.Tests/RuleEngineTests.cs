using NAudio.CoreAudioApi;
using WindowsAudioSwitcher.Audio;
using WindowsAudioSwitcher.Rules;
using Xunit;

namespace WindowsAudioSwitcher.Tests;

public class RuleEngineTests
{
    private static AudioDevice Dev(string id, string name) =>
        new(id, name, DataFlow.Render, IsDefault: false);

    private static DeviceSnapshot Snap(string defaultId, params AudioDevice[] devices) =>
        new(devices, defaultId);

    [Fact]
    public void PickTarget_EmptyPriorityList_ReturnsNull()
    {
        var snap = Snap("a", Dev("a", "A"));
        Assert.Null(RuleEngine.PickTarget(new List<Rule>(), snap));
    }

    [Fact]
    public void PickTarget_NoActiveDeviceMatchesAnyRule_ReturnsNull()
    {
        var rules = new List<Rule>
        {
            new() { Kind = RuleKind.NameContains, Value = "Sony" },
        };
        var snap = Snap("a", Dev("a", "Realtek(R) Audio"));
        Assert.Null(RuleEngine.PickTarget(rules, snap));
    }

    [Fact]
    public void PickTarget_HigherPriorityRuleWins_OverLaterMatchingRule()
    {
        // Both rules match, but rule 1 ("Sony") should win because it appears first.
        var rules = new List<Rule>
        {
            new() { Kind = RuleKind.NameContains, Value = "Sony"    },
            new() { Kind = RuleKind.NameContains, Value = "Realtek" },
        };
        var snap = Snap("a", Dev("a", "Realtek(R) Audio"), Dev("b", "Sony WH-1000XM4"));
        var picked = RuleEngine.PickTarget(rules, snap);
        Assert.NotNull(picked);
        Assert.Equal("b", picked!.Id);
    }

    [Fact]
    public void PickTarget_SkipsDisabledRules_FallsThroughToNextEnabled()
    {
        var rules = new List<Rule>
        {
            new() { Kind = RuleKind.NameContains, Value = "Sony",    IsEnabled = false },
            new() { Kind = RuleKind.NameContains, Value = "Realtek", IsEnabled = true  },
        };
        var snap = Snap("a", Dev("a", "Realtek(R) Audio"), Dev("b", "Sony WH-1000XM4"));
        var picked = RuleEngine.PickTarget(rules, snap);
        Assert.NotNull(picked);
        Assert.Equal("a", picked!.Id);
    }

    [Fact]
    public void PickTarget_AllRulesDisabled_ReturnsNull()
    {
        var rules = new List<Rule>
        {
            new() { Kind = RuleKind.NameContains, Value = "Sony", IsEnabled = false },
        };
        var snap = Snap("b", Dev("b", "Sony WH-1000XM4"));
        Assert.Null(RuleEngine.PickTarget(rules, snap));
    }

    [Fact]
    public void PickTarget_ExactDeviceRule_MatchesById()
    {
        const string id = "{0.0.0.00000000}.{abcdef}";
        var rules = new List<Rule>
        {
            new() { Kind = RuleKind.ExactDevice, Value = id, Label = "My Speakers" },
        };
        var snap = Snap(id, Dev(id, "Realtek(R) Audio"));
        var picked = RuleEngine.PickTarget(rules, snap);
        Assert.NotNull(picked);
        Assert.Equal(id, picked!.Id);
    }
}
