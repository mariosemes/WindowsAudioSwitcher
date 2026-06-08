using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Updates;

/// <summary>
/// Queries the public GitHub repo for the latest release and compares it to
/// the running assembly's version. Anonymous request — the repo is public,
/// so no token is needed (and crucially, no token is embedded in the shipped
/// exe). GitHub's unauthenticated rate limit is 60 reqs/hour per IP; we only
/// hit the endpoint once at startup, so that's plenty of headroom.
/// </summary>
public static class UpdateChecker
{
    // Public GitHub repo — releases live here, installer attached as an asset.
    // Response shape matches what Gitea returned (tag_name, html_url, draft,
    // prerelease, assets[].browser_download_url) so the parser below is
    // unchanged from the old Gitea code path.
    private const string ApiUrl =
        "https://api.github.com/repos/mariosemes/WindowsAudioSwitcher/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Returns an UpdateInfo if the call succeeds, regardless of whether a newer version is out.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var current = AppVersion.Current;

            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            // GitHub requires a User-Agent on every request; we'd want one anyway.
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("WindowsAudioSwitcher", current.ToString()));
            // GitHub's recommended Accept; falls back to application/json behavior if unset.
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 typically means: the repo has no published releases yet
                // (very first run before any release is cut). Either way, bail.
                Logger.Info($"UpdateChecker: {(int)resp.StatusCode} {resp.ReasonPhrase} from {ApiUrl} — skipping.");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag        = root.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
            var releaseUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
            var draft      = root.TryGetProperty("draft", out var dr) && dr.GetBoolean();
            var prerel     = root.TryGetProperty("prerelease", out var pr) && pr.GetBoolean();
            if (tag is null || releaseUrl is null) return null;
            if (draft) { Logger.Info("UpdateChecker: latest is a draft — ignoring."); return null; }

            if (!AppVersion.TryParseVersion(tag, out var latest))
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
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null) continue;
                    if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    installerUrl  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    installerSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : null;
                    break;
                }
            }

            // release.ps1 writes the installer's SHA-256 into the release notes
            // ("- SHA-256: `<hash>`"). Pull it out so the installer download can be
            // integrity-checked before we run it. Best-effort: null if not present.
            var body = root.TryGetProperty("body", out var bd) ? bd.GetString() : null;
            var expectedSha = ExtractSha256(body);

            return new UpdateInfo(
                CurrentVersion:     current,
                LatestVersion:      latest,
                LatestTag:          tag,
                ReleaseUrl:         releaseUrl,
                InstallerUrl:       installerUrl,
                InstallerSizeBytes: installerSize,
                IsPreRelease:       prerel,
                ExpectedSha256:     expectedSha);
        }
        catch (TaskCanceledException) { return null; }
        catch (HttpRequestException ex)
        {
            Logger.Info($"UpdateChecker: network error — {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"UpdateChecker: unexpected — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pulls a 64-hex-char SHA-256 out of free-form release notes. Tolerates the
    /// label spelled "SHA-256" or "SHA256" with assorted separators / backticks.
    /// </summary>
    private static string? ExtractSha256(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            body,
            @"SHA-?256[^0-9A-Fa-f]{0,12}([0-9A-Fa-f]{64})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
