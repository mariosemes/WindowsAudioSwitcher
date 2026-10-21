using WindowsAudioSwitcher.Audio;

namespace WindowsAudioSwitcher.Rules;

/// <summary>
/// Immutable snapshot of the active devices for one direction at a point in time,
/// plus the current default endpoint IDs. The engine works from one snapshot per
/// pass instead of re-enumerating COM endpoints for every question it asks.
/// </summary>
/// <param name="CurrentDefaultId">Default for the Multimedia role.</param>
/// <param name="CurrentCommunicationsId">Default for the Communications role, or null when not read.</param>
public sealed record DeviceSnapshot(
    IReadOnlyList<AudioDevice> Devices,
    string? CurrentDefaultId,
    string? CurrentCommunicationsId = null)
{
    public static DeviceSnapshot Empty { get; } = new(Array.Empty<AudioDevice>(), null);

    public AudioDevice? Find(string? id) =>
        id == null ? null : Devices.FirstOrDefault(d => RuleEngine.IdEquals(d.Id, id));
}

/// <summary>What one ApplyOne pass decided and did for one direction.</summary>
/// <param name="Target">The device the rules point at, or null when no enabled rule matches.</param>
/// <param name="Switched">True when SetDefault was called and succeeded.</param>
/// <param name="Error">Message when SetDefault threw.</param>
public sealed record SwitchOutcome(AudioDevice? Target, bool Switched, string? Error)
{
    public static SwitchOutcome None { get; } = new(null, false, null);
}

/// <summary>How each rule in a priority list relates to the connected devices.</summary>
public sealed record RuleMatch(RuleStatus Status, IReadOnlyList<AudioDevice> Matches, AudioDevice? Picked);

public sealed class RuleEngine
{
    private readonly Action<AudioDevice, bool> _setDefault;

    /// <param name="setDefault">
    /// Makes a device the default. The bool says whether to include the
    /// Communications role. Injected so the engine can be unit-tested.
    /// </param>
    public RuleEngine(Action<AudioDevice, bool> setDefault) => _setDefault = setDefault;

    internal static bool IdEquals(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walk the priority list and return the device for the first enabled rule
    /// that matches an active device. Pure function.
    /// <para>
    /// One rule can match several endpoints: a Bluetooth headset exposes both a
    /// Stereo and a Hands-Free endpoint, and both contain "Sony". If the current
    /// default is one of them it already satisfies the winning rule, so it is
    /// kept instead of switching to whichever sibling enumerates first.
    /// </para>
    /// </summary>
    public static AudioDevice? PickTarget(IList<Rule> priority, DeviceSnapshot snapshot)
    {
        foreach (var rule in priority)
        {
            if (!rule.IsEnabled) continue;
            AudioDevice? first = null;
            foreach (var d in snapshot.Devices)
            {
                if (!rule.Matches(d.Id, d.FriendlyName)) continue;
                if (IdEquals(d.Id, snapshot.CurrentDefaultId)) return d;
                first ??= d;
            }
            if (first != null) return first;
        }
        return null;
    }

    /// <summary>True when the target isn't yet the default for every role we manage.</summary>
    public static bool NeedsSwitch(AudioDevice target, DeviceSnapshot snapshot, bool includeCommunications)
    {
        if (!IdEquals(snapshot.CurrentDefaultId, target.Id)) return true;
        // A null Communications ID means it wasn't read (or there is none);
        // don't keep re-switching on that basis alone.
        return includeCommunications
            && snapshot.CurrentCommunicationsId != null
            && !IdEquals(snapshot.CurrentCommunicationsId, target.Id);
    }

    /// <summary>
    /// Decides whether a switch is needed for one direction and performs it.
    /// Never throws for a failed switch; the failure is reported in the outcome.
    /// </summary>
    public SwitchOutcome ApplyOne(IList<Rule> priority, DeviceSnapshot snapshot, bool includeCommunications)
    {
        var target = PickTarget(priority, snapshot);
        if (target == null) return SwitchOutcome.None;
        if (!NeedsSwitch(target, snapshot, includeCommunications)) return new SwitchOutcome(target, false, null);
        try
        {
            _setDefault(target, includeCommunications);
            return new SwitchOutcome(target, true, null);
        }
        catch (Exception ex)
        {
            return new SwitchOutcome(target, false, ex.Message);
        }
    }

    /// <summary>
    /// Per-rule status for the settings window. Uses the same winner logic as
    /// <see cref="PickTarget"/>, so the row marked Active is the one the engine acts on.
    /// </summary>
    public static IReadOnlyList<RuleMatch> ComputeStatuses(IList<Rule> priority, DeviceSnapshot? snapshot)
    {
        var result = new List<RuleMatch>(priority.Count);
        if (snapshot == null)
        {
            foreach (var _ in priority) result.Add(new RuleMatch(RuleStatus.Unknown, Array.Empty<AudioDevice>(), null));
            return result;
        }

        var target = PickTarget(priority, snapshot);
        var winnerFound = false;
        foreach (var rule in priority)
        {
            var matches = snapshot.Devices.Where(d => rule.Matches(d.Id, d.FriendlyName)).ToList();
            if (matches.Count == 0)
            {
                result.Add(new RuleMatch(RuleStatus.NotConnected, matches, null));
            }
            else if (!winnerFound && rule.IsEnabled)
            {
                winnerFound = true;
                result.Add(new RuleMatch(RuleStatus.Active, matches, target));
            }
            else
            {
                result.Add(new RuleMatch(RuleStatus.Connected, matches, null));
            }
        }
        return result;
    }
}
