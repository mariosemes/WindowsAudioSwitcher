using NAudio.CoreAudioApi;
using WindowsAudioSwitcher.Audio;
using WindowsAudioSwitcher.Rules;
using Xunit;

namespace WindowsAudioSwitcher.Tests;

public class RuleEngineTests
{
    private static AudioDevice Dev(string id, string name) => new(id, name, DataFlow.Render);

    private static DeviceSnapshot Snap(string? defaultId, params AudioDevice[] devices) =>
        new(devices, defaultId);

    private static Rule Name(string value, bool enabled = true) =>
        new() { Kind = RuleKind.NameContains, Value = value, IsEnabled = enabled };

    private static Rule Exact(string id) => new() { Kind = RuleKind.ExactDevice, Value = id };

    // ---- PickTarget --------------------------------------------------------

    [Fact]
    public void PickTarget_EmptyPriorityList_ReturnsNull()
    {
        var snap = Snap("a", Dev("a", "A"));
        Assert.Null(RuleEngine.PickTarget(new List<Rule>(), snap));
    }

    [Fact]
    public void PickTarget_NoActiveDeviceMatchesAnyRule_ReturnsNull()
    {
        var snap = Snap("a", Dev("a", "Realtek(R) Audio"));
        Assert.Null(RuleEngine.PickTarget(new List<Rule> { Name("Sony") }, snap));
    }

    [Fact]
    public void PickTarget_HigherPriorityRuleWins_OverLaterMatchingRule()
    {
        var rules = new List<Rule> { Name("Sony"), Name("Realtek") };
        var snap = Snap("a", Dev("a", "Realtek(R) Audio"), Dev("b", "Sony WH-1000XM4"));
        Assert.Equal("b", RuleEngine.PickTarget(rules, snap)!.Id);
    }

    [Fact]
    public void PickTarget_SkipsDisabledRules_FallsThroughToNextEnabled()
    {
        var rules = new List<Rule> { Name("Sony", enabled: false), Name("Realtek") };
        var snap = Snap("a", Dev("a", "Realtek(R) Audio"), Dev("b", "Sony WH-1000XM4"));
        Assert.Equal("a", RuleEngine.PickTarget(rules, snap)!.Id);
    }

    [Fact]
    public void PickTarget_AllRulesDisabled_ReturnsNull()
    {
        var snap = Snap("b", Dev("b", "Sony WH-1000XM4"));
        Assert.Null(RuleEngine.PickTarget(new List<Rule> { Name("Sony", enabled: false) }, snap));
    }

    [Fact]
    public void PickTarget_ExactDeviceRule_MatchesById()
    {
        const string id = "{0.0.0.00000000}.{abcdef}";
        var rules = new List<Rule> { new() { Kind = RuleKind.ExactDevice, Value = id, Label = "My Speakers" } };
        var snap = Snap(id, Dev(id, "Realtek(R) Audio"));
        Assert.Equal(id, RuleEngine.PickTarget(rules, snap)!.Id);
    }

    [Fact]
    public void PickTarget_SeveralEndpointsMatch_KeepsCurrentDefaultIfItMatches()
    {
        // A Bluetooth headset's two endpoints both contain "Sony". The user (or
        // Windows) picked Hands-Free; the rule is satisfied, so don't flip it.
        var snap = Snap("handsfree",
            Dev("stereo", "Headphones (Sony WH-1000XM4 Stereo)"),
            Dev("handsfree", "Headset (Sony WH-1000XM4 Hands-Free)"));
        Assert.Equal("handsfree", RuleEngine.PickTarget(new List<Rule> { Name("Sony") }, snap)!.Id);
    }

    [Fact]
    public void PickTarget_SeveralEndpointsMatch_DefaultElsewhere_PicksFirstListed()
    {
        var snap = Snap("speakers",
            Dev("speakers", "Speakers (Realtek)"),
            Dev("stereo", "Headphones (Sony WH-1000XM4 Stereo)"),
            Dev("handsfree", "Headset (Sony WH-1000XM4 Hands-Free)"));
        Assert.Equal("stereo", RuleEngine.PickTarget(new List<Rule> { Name("Sony") }, snap)!.Id);
    }

    [Fact]
    public void PickTarget_CurrentDefaultMatchesLowerRule_StillSwitchesToHigherRule()
    {
        // Keeping the current default only applies within the winning rule.
        var rules = new List<Rule> { Name("Sony"), Name("Realtek") };
        var snap = Snap("realtek", Dev("realtek", "Speakers (Realtek)"), Dev("sony", "Sony WH-1000XM4"));
        Assert.Equal("sony", RuleEngine.PickTarget(rules, snap)!.Id);
    }

    // ---- NeedsSwitch / ApplyOne -------------------------------------------

    [Fact]
    public void ApplyOne_TargetAlreadyDefault_DoesNotSwitch()
    {
        var calls = new List<string>();
        var engine = new RuleEngine((d, _) => calls.Add(d.Id));
        var outcome = engine.ApplyOne(new List<Rule> { Exact("a") }, Snap("a", Dev("a", "A")), includeCommunications: false);
        Assert.Empty(calls);
        Assert.False(outcome.Switched);
        Assert.Equal("a", outcome.Target!.Id);
    }

    [Fact]
    public void ApplyOne_TargetNotDefault_SwitchesAndPassesCommunicationsFlag()
    {
        var calls = new List<(string Id, bool Comms)>();
        var engine = new RuleEngine((d, comms) => calls.Add((d.Id, comms)));
        var outcome = engine.ApplyOne(new List<Rule> { Exact("b") }, Snap("a", Dev("a", "A"), Dev("b", "B")), includeCommunications: true);
        Assert.Equal(new[] { ("b", true) }, calls);
        Assert.True(outcome.Switched);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void ApplyOne_NoMatchingRule_ReturnsNoneWithoutSwitching()
    {
        var engine = new RuleEngine((_, _) => throw new InvalidOperationException("should not be called"));
        var outcome = engine.ApplyOne(new List<Rule> { Name("Sony") }, Snap("a", Dev("a", "A")), includeCommunications: true);
        Assert.Same(SwitchOutcome.None, outcome);
    }

    [Fact]
    public void ApplyOne_SetDefaultThrows_ReportsErrorInsteadOfThrowing()
    {
        var engine = new RuleEngine((_, _) => throw new InvalidOperationException("Element not found."));
        var outcome = engine.ApplyOne(new List<Rule> { Exact("b") }, Snap("a", Dev("a", "A"), Dev("b", "B")), includeCommunications: false);
        Assert.False(outcome.Switched);
        Assert.Equal("b", outcome.Target!.Id);
        Assert.Equal("Element not found.", outcome.Error);
    }

    [Fact]
    public void NeedsSwitch_CommunicationsRoleOnOtherDevice_SwitchesOnlyWhenIncluded()
    {
        var target = Dev("a", "A");
        var snap = new DeviceSnapshot(new[] { target, Dev("b", "B") }, CurrentDefaultId: "a", CurrentCommunicationsId: "b");
        Assert.True(RuleEngine.NeedsSwitch(target, snap, includeCommunications: true));
        Assert.False(RuleEngine.NeedsSwitch(target, snap, includeCommunications: false));
    }

    [Fact]
    public void NeedsSwitch_CommunicationsUnknown_DoesNotForceSwitch()
    {
        var target = Dev("a", "A");
        var snap = new DeviceSnapshot(new[] { target }, CurrentDefaultId: "a", CurrentCommunicationsId: null);
        Assert.False(RuleEngine.NeedsSwitch(target, snap, includeCommunications: true));
    }

    [Fact]
    public void NeedsSwitch_IdComparisonIsCaseInsensitive()
    {
        var target = Dev("{ABC}", "A");
        Assert.False(RuleEngine.NeedsSwitch(target, Snap("{abc}", target), includeCommunications: false));
    }

    // ---- ComputeStatuses ---------------------------------------------------

    [Fact]
    public void ComputeStatuses_NoSnapshot_AllUnknown()
    {
        var statuses = RuleEngine.ComputeStatuses(new List<Rule> { Name("Sony"), Exact("a") }, snapshot: null);
        Assert.All(statuses, s => Assert.Equal(RuleStatus.Unknown, s.Status));
    }

    [Fact]
    public void ComputeStatuses_MarksWinnerActive_OthersConnectedOrNot()
    {
        var rules = new List<Rule>
        {
            Name("Bose"),                    // nothing connected
            Name("Sony", enabled: false),    // connected but off
            Name("Realtek"),                 // winner
            Exact("sony"),                   // connected, outranked
        };
        var snap = Snap("realtek", Dev("realtek", "Speakers (Realtek)"), Dev("sony", "Sony WH-1000XM4"));

        var statuses = RuleEngine.ComputeStatuses(rules, snap);

        Assert.Equal(
            new[] { RuleStatus.NotConnected, RuleStatus.Connected, RuleStatus.Active, RuleStatus.Connected },
            statuses.Select(s => s.Status));
        Assert.Equal("realtek", statuses[2].Picked!.Id);
    }

    [Fact]
    public void ComputeStatuses_ActiveRulePicksSameDeviceAsEngine()
    {
        var rules = new List<Rule> { Name("Sony") };
        var snap = Snap("handsfree", Dev("stereo", "Sony Stereo"), Dev("handsfree", "Sony Hands-Free"));
        var status = RuleEngine.ComputeStatuses(rules, snap).Single();
        Assert.Equal(RuleStatus.Active, status.Status);
        Assert.Equal(RuleEngine.PickTarget(rules, snap)!.Id, status.Picked!.Id);
        Assert.Equal(2, status.Matches.Count);
    }
}
