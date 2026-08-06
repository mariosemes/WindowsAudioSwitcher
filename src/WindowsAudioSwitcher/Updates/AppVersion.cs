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
        version = new Version(0, 0, 0);
        if (text == null) return false;
        var m = Regex.Match(text, @"^v?(\d+)\.(\d+)\.(\d+)");
        // int.TryParse (not int.Parse) so an absurdly long numeric segment yields a
        // clean "false" instead of throwing out of a method named Try*.
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, out var maj)
            || !int.TryParse(m.Groups[2].Value, out var min)
            || !int.TryParse(m.Groups[3].Value, out var pat))
        {
            return false;
        }
        version = new Version(maj, min, pat);
        return true;
    }
}
