using Microsoft.Win32;

namespace WindowsAudioSwitcher.Startup;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsAudioSwitcher";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{exe}\" --tray");
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void Sync(bool shouldRunOnStartup)
    {
        if (shouldRunOnStartup) Register();
        else Unregister();
    }
}
