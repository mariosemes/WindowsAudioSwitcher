namespace WindowsAudioSwitcher.Updates;

public sealed record UpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string LatestTag,
    string ReleaseUrl,
    string? InstallerUrl,
    long? InstallerSizeBytes,
    bool IsPreRelease)
{
    public bool IsUpdateAvailable
    {
        get
        {
            // .NET Version compares Build as -1 when undefined. We treat missing
            // segments as 0 and ignore Revision so 0.4.0 (parsed) == 0.4.0.0
            // (assembly) and a real 0.4.1 still wins.
            var cMaj = CurrentVersion.Major;
            var cMin = CurrentVersion.Minor;
            var cPat = Math.Max(0, CurrentVersion.Build);
            var lMaj = LatestVersion.Major;
            var lMin = LatestVersion.Minor;
            var lPat = Math.Max(0, LatestVersion.Build);
            if (lMaj != cMaj) return lMaj > cMaj;
            if (lMin != cMin) return lMin > cMin;
            return lPat > cPat;
        }
    }
}
