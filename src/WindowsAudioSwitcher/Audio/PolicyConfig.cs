using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Audio;

/// <summary>
/// Sets the Windows default audio endpoint via the undocumented IPolicyConfig
/// COM interface. Windows 11 22H2/24H2 changed which IID the PolicyConfig
/// COM server responds to, so we try the modern (Vista) interface first and
/// fall back to the original interface used on older builds.
/// </summary>
internal static class PolicyConfig
{
    private static volatile string? _workingInterface;

    public static void SetDefaultDevice(string deviceId)
    {
        object? client = null;
        try
        {
            client = new PolicyConfigClient();

            if (client is IPolicyConfigVista vista)
            {
                if (_workingInterface != "Vista")
                {
                    Logger.Info("Using IPolicyConfigVista (568b9108-44bf-40b4-9006-86afe5b5a620)");
                    _workingInterface = "Vista";
                }
                SetAllRoles(deviceId, (id, role) => vista.SetDefaultEndpoint(id, role));
                return;
            }

            if (client is IPolicyConfig legacy)
            {
                if (_workingInterface != "Legacy")
                {
                    Logger.Info("Using legacy IPolicyConfig (f8679f50-850a-41cf-9c72-430f290290c8)");
                    _workingInterface = "Legacy";
                }
                SetAllRoles(deviceId, (id, role) => legacy.SetDefaultEndpoint(id, role));
                return;
            }

            throw new NotSupportedException(
                "The PolicyConfig COM server on this Windows build does not expose either " +
                "IPolicyConfigVista or IPolicyConfig. Please file an issue with your Windows " +
                $"build number ({Environment.OSVersion.Version}).");
        }
        finally
        {
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    // Switch all three roles, attempting every one even if an earlier role fails,
    // so we don't leave the system half-switched (e.g. Console on the new device
    // but Communications still on the old). Any failures are aggregated and thrown
    // so callers (and the user-facing error toast) still learn the switch failed.
    private static readonly Role[] AllRoles = { Role.Console, Role.Multimedia, Role.Communications };

    private static void SetAllRoles(string deviceId, Func<string, Role, int> setEndpoint)
    {
        List<Exception>? failures = null;
        foreach (var role in AllRoles)
        {
            var hr = setEndpoint(deviceId, role);
            if (hr < 0)
            {
                var ex = Marshal.GetExceptionForHR(hr)
                         ?? new COMException($"SetDefaultEndpoint({role}) failed", hr);
                Logger.Warn($"SetDefaultEndpoint({role}) failed: 0x{hr:X8} — {ex.Message}");
                (failures ??= new List<Exception>()).Add(ex);
            }
        }
        if (failures != null)
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("One or more audio role switches failed.", failures);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    // Vista+ flavour. SetDefaultEndpoint is at vtable slot 13.
    [Guid("568b9108-44bf-40b4-9006-86afe5b5a620"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool defaultFormat, IntPtr format);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format, IntPtr format2);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool defaultPeriod, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    // Original IPolicyConfig has one more method (ResetDeviceFormat) before
    // SetDeviceFormat, so SetDefaultEndpoint moves to slot 14.
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format, IntPtr format2);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool defaultPeriod, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
