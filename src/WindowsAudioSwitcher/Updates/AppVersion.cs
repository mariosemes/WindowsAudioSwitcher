using System.Reflection;

namespace WindowsAudioSwitcher.Updates;

/// <summary>
/// Single source for the running app's semantic version. Prefers the
/// AssemblyInformationalVersion attribute (stamped by release.ps1 during
/// publish, pre-release tag included) and falls back to the assembly version.
/// </summary>
public static class AppVersion
{
    public static SemVersion Current { get; } = Resolve();

    /// <summary>"0.6.0", or "0.6.0-rc1" for a pre-release build. No "v" prefix.</summary>
    public static string Display => Current.ToString();

    private static SemVersion Resolve()
    {
        var asm = typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemVersion.TryParse(info, out var v)) return v;
        var n = asm.GetName().Version;
        return n == null ? new SemVersion(0, 0, 0) : new SemVersion(n.Major, n.Minor, Math.Max(0, n.Build));
    }
}
