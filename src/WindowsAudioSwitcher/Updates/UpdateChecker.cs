using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Updates;

/// <summary>
/// Queries the public GitHub repo for the latest release and compares it to
/// the running assembly's version. Anonymous request: the repo is public, so
/// no token is needed (and none is embedded in the shipped exe). GitHub's
/// unauthenticated limit is 60 requests/hour per IP; we check at startup and
/// then once a day, so that's plenty of headroom.
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl =
        "https://api.github.com/repos/" + UpdateUrls.Owner + "/" + UpdateUrls.Repository + "/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Returns an UpdateInfo if the call succeeds, regardless of whether a newer version is out.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            // GitHub requires a User-Agent on every request.
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("WindowsAudioSwitcher", AppVersion.Display));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 means the repo has no published releases yet. Either way, bail.
                Logger.Info($"UpdateChecker: {(int)resp.StatusCode} {resp.ReasonPhrase} from {ApiUrl}; skipping.");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseRelease(json, AppVersion.Current);
        }
        catch (OperationCanceledException) { return null; }
        catch (HttpRequestException ex)
        {
            Logger.Info($"UpdateChecker: network error: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"UpdateChecker: unexpected: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turns a GitHub "latest release" JSON document into an UpdateInfo, or
    /// null when the release is unusable (draft, unparsable tag, or a release
    /// page outside this repository). An installer URL that fails the
    /// allow-list is dropped, which makes the UI fall back to the release page.
    /// </summary>
    internal static UpdateInfo? ParseRelease(string json, SemVersion current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var tag        = GetString(root, "tag_name");
        var releaseUrl = GetString(root, "html_url");
        var draft      = root.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True;
        var prerel     = root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
        if (tag is null || releaseUrl is null) return null;
        if (draft) { Logger.Info("UpdateChecker: latest is a draft; ignoring."); return null; }

        if (!UpdateUrls.IsTrustedReleasePage(releaseUrl))
        {
            Logger.Warn($"UpdateChecker: release URL '{releaseUrl}' is not on the expected GitHub repo; ignoring release.");
            return null;
        }

        if (!SemVersion.TryParse(tag, out var latest))
        {
            Logger.Warn($"UpdateChecker: could not parse tag '{tag}' as a version.");
            return null;
        }

        string? installerUrl  = null;
        long?   installerSize = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (name == null || !name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                installerUrl = GetString(asset, "browser_download_url");
                if (asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var size))
                    installerSize = size;
                break;
            }
        }

        if (installerUrl != null && !UpdateUrls.IsTrustedDownload(installerUrl))
        {
            Logger.Warn($"UpdateChecker: installer URL '{installerUrl}' is not on the expected GitHub repo; falling back to the release page.");
            installerUrl = null;
            installerSize = null;
        }

        // release.ps1 writes the installer's SHA-256 into the release notes
        // ("- SHA-256: `<hash>`") so the download can be integrity-checked.
        var expectedSha = ExtractSha256(GetString(root, "body"));

        return new UpdateInfo(
            CurrentVersion:     current,
            LatestVersion:      latest,
            ReleaseUrl:         releaseUrl,
            InstallerUrl:       installerUrl,
            InstallerSizeBytes: installerSize,
            IsPreRelease:       prerel || latest.IsPreRelease,
            ExpectedSha256:     expectedSha);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    /// <summary>
    /// Pulls a 64-hex-char SHA-256 out of free-form release notes. Tolerates the
    /// label spelled "SHA-256" or "SHA256" with assorted separators / backticks.
    /// A longer hex run (e.g. a SHA-512) is rejected rather than truncated.
    /// </summary>
    internal static string? ExtractSha256(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = Regex.Match(body, @"SHA-?256[^0-9A-Fa-f]{0,12}([0-9A-Fa-f]{64})(?![0-9A-Fa-f])", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
