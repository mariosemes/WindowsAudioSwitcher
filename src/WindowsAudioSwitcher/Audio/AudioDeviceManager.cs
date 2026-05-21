using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WindowsAudioSwitcher.Logging;
using WindowsAudioSwitcher.Rules;

namespace WindowsAudioSwitcher.Audio;

/// <summary>
/// Enumerates audio endpoints, raises events on device changes, and switches default I/O.
/// Marshals all callbacks onto a single SynchronizationContext (typically the WPF dispatcher).
/// </summary>
public sealed class AudioDeviceManager : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly SynchronizationContext? _sync;
    private bool _disposed;

    public event Action? DevicesChanged;

    public AudioDeviceManager(SynchronizationContext? sync)
    {
        _sync = sync;
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public IReadOnlyList<AudioDevice> GetDevices(DataFlow flow)
    {
        var defaultId = TryGetDefaultId(flow);
        var list = new List<AudioDevice>();
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                list.Add(new AudioDevice(d.ID, d.FriendlyName, flow, d.ID == defaultId));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"EnumerateAudioEndPoints({flow}) failed", ex);
        }
        return list;
    }

    /// <summary>Enumerate active endpoints and read the default ID in one COM pass.</summary>
    public DeviceSnapshot Snapshot(DataFlow flow)
    {
        var defaultId = TryGetDefaultId(flow);
        var list = new List<AudioDevice>();
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                list.Add(new AudioDevice(d.ID, d.FriendlyName, flow, d.ID == defaultId));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Snapshot({flow}) failed", ex);
        }
        return new DeviceSnapshot(list, defaultId);
    }

    public string? TryGetDefaultId(DataFlow flow)
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID;
        }
        catch (Exception ex)
        {
            // E_NOTFOUND is normal for a flow with no devices (e.g. no mic) — log at debug only.
            Logger.Info($"GetDefaultAudioEndpoint({flow}) returned no device ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public void SetDefault(string deviceId)
    {
        Logger.Info($"SetDefault({deviceId}) — calling IPolicyConfigVista");
        try
        {
            PolicyConfig.SetDefaultDevice(deviceId);
            Logger.Info($"SetDefault({deviceId}) OK");
        }
        catch (Exception ex)
        {
            Logger.Error($"SetDefault({deviceId}) failed", ex);
            throw;
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
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Raise();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator.Dispose();
    }
}
