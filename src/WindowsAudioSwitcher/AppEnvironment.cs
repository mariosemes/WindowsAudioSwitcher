using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WindowsAudioSwitcher;

/// <summary>
/// Where the app keeps its data, and whether it is running sandboxed.
/// <para>
/// Setting the <see cref="DataDirVariable"/> environment variable redirects
/// settings and logs to that folder and isolates the instance: it gets its own
/// single-instance mutex and never touches the HKCU Run key. That lets a dev
/// build run side by side with the installed app without disturbing it, and
/// keeps the unit tests out of the user's real settings and logs.
/// </para>
/// </summary>
public static class AppEnvironment
{
    public const string DataDirVariable = "WINDOWSAUDIOSWITCHER_DATA_DIR";

    // Resolved once. Field order matters: InstanceSuffix below reads it.
    private static readonly string? Sandbox = ResolveSandbox();

    public static bool IsSandboxed => Sandbox != null;

    public static string DataDirectory => Sandbox ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsAudioSwitcher");

    /// <summary>
    /// Appended to named kernel objects (mutex, event) so a sandboxed instance
    /// doesn't collide with the installed one. Empty when not sandboxed.
    /// </summary>
    public static string InstanceSuffix { get; } = Sandbox == null ? string.Empty : "." + ShortHash(Sandbox);

    private static string? ResolveSandbox()
    {
        var dir = Environment.GetEnvironmentVariable(DataDirVariable);
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try { return Path.GetFullPath(dir.Trim()); }
        catch { return null; }
    }

    private static string ShortHash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToUpperInvariant())))[..12];
}
