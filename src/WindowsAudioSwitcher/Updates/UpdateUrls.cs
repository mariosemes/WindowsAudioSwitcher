namespace WindowsAudioSwitcher.Updates;

/// <summary>
/// Allow-list for URLs that arrive in the GitHub API response. A release-page
/// URL is handed to ShellExecute and an installer URL is downloaded and run, so
/// both must point at this repository on github.com over HTTPS. The SHA-256
/// check covers integrity of the bytes; this covers where we send the user.
/// </summary>
internal static class UpdateUrls
{
    public const string Owner = "mariosemes";
    public const string Repository = "WindowsAudioSwitcher";

    private const string RepoPathPrefix = "/" + Owner + "/" + Repository + "/";

    /// <summary>e.g. https://github.com/mariosemes/WindowsAudioSwitcher/releases/tag/v0.6.0</summary>
    public static bool IsTrustedReleasePage(string? url) => IsRepoUrl(url, "releases");

    /// <summary>e.g. https://github.com/mariosemes/WindowsAudioSwitcher/releases/download/v0.6.0/name-setup.exe</summary>
    public static bool IsTrustedDownload(string? url) => IsRepoUrl(url, "releases/download/");

    private static bool IsRepoUrl(string? url, string sub)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        // Uri normalises "/../" segments in AbsolutePath, so a traversal out of
        // the repo prefix fails the StartsWith check below.
        return uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath.StartsWith(RepoPathPrefix + sub, StringComparison.OrdinalIgnoreCase);
    }
}
