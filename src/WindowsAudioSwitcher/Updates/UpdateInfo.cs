namespace WindowsAudioSwitcher.Updates;

public sealed record UpdateInfo(
    SemVersion CurrentVersion,
    SemVersion LatestVersion,
    string ReleaseUrl,
    string? InstallerUrl,
    long? InstallerSizeBytes,
    bool IsPreRelease,
    // SHA-256 of the installer as published in the release notes, if present.
    // Null for releases that don't publish one (older releases / custom notes).
    string? ExpectedSha256 = null)
{
    /// <summary>SemVer precedence, so a 0.6.0-rc1 build is offered the final 0.6.0.</summary>
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}
