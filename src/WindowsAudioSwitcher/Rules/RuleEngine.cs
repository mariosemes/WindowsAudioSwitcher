using NAudio.CoreAudioApi;
using WindowsAudioSwitcher.Audio;

namespace WindowsAudioSwitcher.Rules;

/// <summary>
/// Immutable snapshot of the active devices for one direction at a point in time,
/// plus the current default endpoint ID. Passed into the engine so we enumerate
/// COM endpoints once per Apply pass instead of four times.
/// </summary>
public sealed record DeviceSnapshot(IReadOnlyList<AudioDevice> Devices, string? CurrentDefaultId);

public sealed class RuleEngine
{
    private readonly AudioDeviceManager _devices;

    public RuleEngine(AudioDeviceManager devices) => _devices = devices;

    /// <summary>
    /// Walk the priority list, return the first enabled rule that matches an
    /// active device. Pure function — easy to unit-test.
    /// </summary>
    public static AudioDevice? PickTarget(IList<Rule> priority, DeviceSnapshot snapshot)
    {
        if (priority.Count == 0) return null;
        foreach (var rule in priority)
        {
            if (!rule.IsEnabled) continue;
            foreach (var d in snapshot.Devices)
            {
                if (rule.Matches(d.Id, d.FriendlyName)) return d;
            }
        }
        return null;
    }

    /// <summary>
    /// Decides whether a switch is needed for one flow and performs it.
    /// Returns the device we switched to, or null if no rule matched or the
    /// chosen device was already the default.
    /// </summary>
    public AudioDevice? ApplyOne(IList<Rule> priority, DeviceSnapshot snapshot)
    {
        var target = PickTarget(priority, snapshot);
        if (target == null) return null;
        if (string.Equals(snapshot.CurrentDefaultId, target.Id, StringComparison.OrdinalIgnoreCase)) return null;
        _devices.SetDefault(target.Id);
        return target;
    }
}
