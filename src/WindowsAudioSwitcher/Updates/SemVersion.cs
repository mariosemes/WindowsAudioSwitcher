using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowsAudioSwitcher.Updates;

/// <summary>
/// A semantic version (MAJOR.MINOR.PATCH with an optional pre-release tag)
/// ordered by SemVer 2.0 precedence: 0.6.0-rc1 &lt; 0.6.0 &lt; 0.6.1.
/// Build metadata ("+sha", which the .NET SDK appends to the informational
/// version) is accepted and ignored.
/// </summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemVersion>
{
    private static readonly Regex Pattern = new(
        @"^\s*v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]*)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern.Match(text);
        // int.TryParse so an absurdly long numeric segment yields a clean false.
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }
        var pre = m.Groups[4].Success ? m.Groups[4].Value : null;
        version = new SemVersion(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    // SemVer 2.0 section 11: a version without a pre-release tag outranks one
    // with it. Dot-separated identifiers compare numerically when both are
    // numeric, numeric ranks below alphanumeric, and a longer list wins a tie.
    private static int ComparePreRelease(string? a, string? b)
    {
        var aEmpty = string.IsNullOrEmpty(a);
        var bEmpty = string.IsNullOrEmpty(b);
        if (aEmpty || bEmpty) return aEmpty == bEmpty ? 0 : (aEmpty ? 1 : -1);

        var pa = a!.Split('.');
        var pb = b!.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var aNum = int.TryParse(pa[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bNum = int.TryParse(pb[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            var c = (aNum, bNum) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(pa[i], pb[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return pa.Length.CompareTo(pb.Length);
    }

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
