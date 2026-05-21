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
                Marshal.ThrowExceptionForHR(vista.SetDefaultEndpoint(deviceId, Role.Console));
                Marshal.ThrowExceptionForHR(vista.SetDefaultEndpoint(deviceId, Role.Multimedia));
                Marshal.ThrowExceptionForHR(vista.SetDefaultEndpoint(deviceId, Role.Communications));
                return;
            }

            if (client is IPolicyConfig legacy)
            {
                if (_workingInterface != "Legacy")
                {
                    Logger.Info("Using legacy IPolicyConfig (f8679f50-850a-41cf-9c72-430f290290c8)");
                    _workingInterface = "Legacy";
                }
                Marshal.ThrowExceptionForHR(legacy.SetDefaultEndpoint(deviceId, Role.Console));
                Marshal.ThrowExceptionForHR(legacy.SetDefaultEndpoint(deviceId, Role.Multimedia));
                Marshal.ThrowExceptionForHR(legacy.SetDefaultEndpoint(deviceId, Role.Communications));
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
