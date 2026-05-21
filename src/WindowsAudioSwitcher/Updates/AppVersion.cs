using System.Reflection;
using System.Text.RegularExpressions;

namespace WindowsAudioSwitcher.Updates;

/// <summary>
/// Single source for the running app's semantic version. Prefers the
/// AssemblyInformationalVersion attribute (stamped by release.ps1 during
/// publish) and falls back to the 4-part assembly version. Pre-release /
/// build-metadata suffixes (e.g. "-rc1", "+sha") are stripped.
/// </summary>
public static class AppVersion
{
    public static Version Current { get; } = Resolve();

    /// <summary>"0.5.0" or similar — the version without any v prefix or suffix.</summary>
    public static string Display => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";

    private static Version Resolve()
    {
        var asm = typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info != null && TryParseVersion(info, out var v)) return v;
        return asm.GetName().Version ?? new Version(0, 0, 0);
    }

    public static bool TryParseVersion(string text, out Version version)
    {
        var m = Regex.Match(text, @"^v?(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) { version = new Version(0, 0, 0); return false; }
        version = new Version(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value));
        return true;
    }
}
