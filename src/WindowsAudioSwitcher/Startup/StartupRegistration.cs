using Microsoft.Win32;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Startup;

/// <summary>
/// Manages the HKCU Run-key entry that starts the app (to the tray) at sign-in.
/// The installer's "Launch on sign-in" task writes the same value.
/// </summary>
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
        var desired = $"\"{exe}\" --tray";
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        // Rewriting on every startup keeps the path right if the install moved,
        // but skip the write when nothing changed.
        if (key.GetValue(ValueName) as string == desired) return;
        key.SetValue(ValueName, desired);
        Logger.Info($"Run key set to {desired}");
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is null) return;
        key.DeleteValue(ValueName, throwOnMissingValue: false);
        Logger.Info("Run key removed.");
    }

    public static void Sync(bool shouldRunOnStartup)
    {
        // A sandboxed dev build must never point the user's real autostart
        // entry at itself.
        if (AppEnvironment.IsSandboxed) return;
        if (shouldRunOnStartup) Register();
        else Unregister();
    }
}
