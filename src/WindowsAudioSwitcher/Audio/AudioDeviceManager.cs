using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WindowsAudioSwitcher.Logging;
using WindowsAudioSwitcher.Rules;

namespace WindowsAudioSwitcher.Audio;

/// <summary>
/// Enumerates audio endpoints, raises events on device changes, and switches
/// the default device. Events are marshalled onto the SynchronizationContext
/// passed in (the WPF dispatcher).
/// <para>
/// Thread-safety: <see cref="Snapshot"/>, <see cref="TryGetDefaultId"/> and
/// <see cref="SetDefault"/> may be called from any thread. Each snapshot uses
/// its own short-lived enumerator created on the calling thread, so the rule
/// engine can run on a worker thread while the UI enumerates for a dialog.
/// Both COM classes involved are registered ThreadingModel=Both.
/// </para>
/// </summary>
public sealed class AudioDeviceManager : IDisposable, IMMNotificationClient
{
    // Long enough to cover the burst of OnDefaultDeviceChanged callbacks
    // (one per role) that follows our own SetDefault.
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(3);

    // Long-lived enumerator used only to receive endpoint notifications.
    private readonly MMDeviceEnumerator _notifier = new();
    private readonly SynchronizationContext? _sync;
    private readonly object _echoLock = new();
    private readonly Dictionary<DataFlow, (string Id, DateTime UntilUtc)> _expectedDefaults = new();
    private bool _disposed;

    public event Action? DevicesChanged;

    public AudioDeviceManager(SynchronizationContext? sync)
    {
        _sync = sync;
        _notifier.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>Enumerate active endpoints and read the default IDs in one pass.</summary>
    /// <param name="includeCommunications">Also read the Communications-role default.</param>
    public DeviceSnapshot Snapshot(DataFlow flow, bool includeCommunications = false)
    {
        var list = new List<AudioDevice>();
        string? defaultId = null;
        string? commsId = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            defaultId = TryGetDefaultId(enumerator, flow, Role.Multimedia);
            if (includeCommunications) commsId = TryGetDefaultId(enumerator, flow, Role.Communications);

            foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                // MMDevice wraps COM handles (an IMMDevice plus a lazily-opened
                // IPropertyStore once FriendlyName is read). Copy what we need,
                // then dispose so the handles are released now rather than at GC.
                using (d)
                {
                    try
                    {
                        list.Add(new AudioDevice(d.ID, d.FriendlyName, flow));
                    }
                    catch (Exception ex)
                    {
                        // A device unplugged mid-enumeration fails its property
                        // read. Skip it instead of losing the whole snapshot.
                        Logger.Warn($"Snapshot({flow}): skipping a device whose properties could not be read: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Snapshot({flow}) failed", ex);
        }
        return new DeviceSnapshot(list, defaultId, commsId);
    }

    public string? TryGetDefaultId(DataFlow flow, Role role = Role.Multimedia)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return TryGetDefaultId(enumerator, flow, role);
        }
        catch (Exception ex)
        {
            Logger.Error($"TryGetDefaultId({flow}, {role}) failed", ex);
            return null;
        }
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            // GetDefaultAudioEndpoint returns an IDisposable MMDevice; dispose it
            // once we've pulled the ID so its COM handle isn't held until GC.
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch (Exception ex)
        {
            Logger.Info($"GetDefaultAudioEndpoint({flow}, {role}) returned no device ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Makes <paramref name="device"/> the default for Console and Multimedia,
    /// and for Communications when <paramref name="includeCommunications"/> is set.
    /// Throws if any role fails.
    /// </summary>
    public void SetDefault(AudioDevice device, bool includeCommunications)
    {
        // Record the switch before making it: the resulting
        // OnDefaultDeviceChanged callbacks arrive on another thread, possibly
        // before PolicyConfig returns. Those echoes need no re-evaluation.
        lock (_echoLock) _expectedDefaults[device.Flow] = (device.Id, DateTime.UtcNow + EchoWindow);

        Logger.Info($"SetDefault({device.Id}, {device.Flow}, communications={includeCommunications})");
        try
        {
            PolicyConfig.SetDefaultDevice(device.Id, includeCommunications);
            Logger.Info($"SetDefault({device.Id}) OK");
        }
        catch (Exception ex)
        {
            Logger.Error($"SetDefault({device.Id}) failed", ex);
            throw;
        }
    }

    private bool IsEchoOfOwnSwitch(DataFlow flow, string? newDefaultId)
    {
        if (newDefaultId == null) return false;
        lock (_echoLock)
        {
            return _expectedDefaults.TryGetValue(flow, out var expected)
                && DateTime.UtcNow <= expected.UntilUtc
                && string.Equals(expected.Id, newDefaultId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Raise([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Logger.Info($"IMMNotificationClient.{caller}");
        try
        {
            if (_sync != null) _sync.Post(_ =>
            {
                try { DevicesChanged?.Invoke(); }
                catch (Exception ex) { Logger.Error($"DevicesChanged handler threw ({caller})", ex); }
            }, null);
            else DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error($"Raise({caller}) failed", ex);
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Raise();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Raise();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Our own switch fires one of these per role. The new default is
        // exactly what the rules asked for, so a follow-up pass would only
        // re-enumerate and conclude "no change". Anything else (the user or
        // another app picking a different device) still triggers a pass.
        if (IsEchoOfOwnSwitch(flow, defaultDeviceId))
        {
            Logger.Info($"IMMNotificationClient.OnDefaultDeviceChanged({flow}, {role}): echo of our own switch; ignored.");
            return;
        }
        Raise();
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Only a friendly-name change matters: name rules match on it, and a
        // Bluetooth endpoint can get its real name just after it becomes active.
        // Other properties change constantly and would only cause empty passes.
        var name = PropertyKeys.PKEY_Device_FriendlyName;
        if (key.formatId == name.formatId && key.propertyId == name.propertyId) Raise();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _notifier.UnregisterEndpointNotificationCallback(this); } catch { }
        _notifier.Dispose();
    }
}
