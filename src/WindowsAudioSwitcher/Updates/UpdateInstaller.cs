using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Windows;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Updates;

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is long t && t > 0
        ? (double)BytesReceived / t * 100.0
        : null;
}

/// <summary>
/// Downloads the installer for a pending update to %TEMP% and launches it
/// silently. Inno Setup's CloseApplications=force kills the running app
/// mid-install; the installer's silent [Run] entry brings it back (with --tray)
/// at the new version.
/// </summary>
public static class UpdateInstaller
{
    private static readonly HttpClient Http = new()
    {
        // Big enough for a 70 MB installer over a so-so connection.
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// Streams <paramref name="url"/> to a fresh file in %TEMP% and returns the
    /// path. Reports byte counts to <paramref name="progress"/>. The downloaded
    /// bytes are verified against <paramref name="expectedSha256"/> and the file
    /// is deleted (and an exception thrown) on mismatch — so a corrupted or
    /// swapped download is never handed to the launcher.
    /// <para>
    /// Verification is MANDATORY: if no hash is supplied (the release didn't
    /// publish one) this throws <em>before</em> downloading rather than fetching
    /// and silently running an unverifiable installer. Callers fall back to
    /// opening the release page so the user installs manually. Note this is
    /// integrity, not authenticity — a release-repo compromise could edit both
    /// the bytes and the published hash; only code-signing would cover that.
    /// </para>
    /// </summary>
    public static async Task<string> DownloadAsync(
        string url,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Fail closed: without a hash we cannot verify the download, so we refuse
        // to run it. Bail before spending bandwidth on bytes we won't launch.
        if (string.IsNullOrEmpty(expectedSha256))
        {
            Logger.Warn("UpdateInstaller: release published no SHA-256 — refusing to auto-run an unverifiable installer.");
            throw new InvalidOperationException(
                "This release didn't publish a verification hash, so the installer can't be verified automatically. " +
                "Install it manually from the release page.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "WindowsAudioSwitcher-update");
        Directory.CreateDirectory(tempDir);
        var fileName = SafeNameFromUrl(url);
        var dest = Path.Combine(tempDir, fileName);

        // If a prior partial download is sitting around, overwrite it.
        if (File.Exists(dest)) File.Delete(dest);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("WindowsAudioSwitcher", AppVersion.Display));

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // FileStream with FileShare.Read so the user/Defender can poke at it
        // while it downloads if they really want to.
        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);

        var buf = new byte[81920];
        long received = 0;
        progress?.Report(new DownloadProgress(0, total));
        while (true)
        {
            var n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) break;
            await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            received += n;
            progress?.Report(new DownloadProgress(received, total));
        }

        Logger.Info($"UpdateInstaller: downloaded {received} bytes to {dest}");

        // Integrity check (mandatory — a missing hash was already rejected above).
        // Guards against a corrupted/partial download or a swapped asset. Verify
        // before we ever hand the file to the silent launcher.
        var actual = await ComputeSha256Async(dest, ct).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(dest); } catch { /* best effort */ }
            Logger.Error($"UpdateInstaller: SHA-256 mismatch (expected {expectedSha256}, got {actual}) — deleted download.");
            throw new InvalidOperationException(
                "The downloaded installer failed its integrity check (SHA-256 mismatch). " +
                "It may be corrupted — it was not run.");
        }
        Logger.Info("UpdateInstaller: SHA-256 verified.");

        return dest;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash); // uppercase hex; compared case-insensitively
    }

    /// <summary>
    /// Launches the downloaded installer silently and shuts the current
    /// WPF app down. The installer's CloseApplications=force will terminate
    /// our process if Shutdown hasn't finished by the time it tries to write.
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            // Inno Setup silent install. SUPPRESSMSGBOXES auto-answers any upgrade
            // prompts. We deliberately do NOT pass /RESTARTAPPLICATIONS: the installer
            // re-launches us via its silent [Run] entry (with --tray) so we come back
            // minimized to the tray instead of via the Restart Manager.
            Arguments = "/SILENT /SUPPRESSMSGBOXES",
            UseShellExecute = true,
        };
        Logger.Info($"UpdateInstaller: launching {installerPath} {psi.Arguments}");
        Process.Start(psi);

        // Cede the UI thread to the installer. The installer will force-close
        // us via CloseApplications=force if we're still running when it needs
        // to write the .exe, but Shutdown lets us finish gracefully first.
        Application.Current.Shutdown();
    }

    private static string SafeNameFromUrl(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).LocalPath);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch { /* fall through */ }
        return $"WindowsAudioSwitcher-update-{Guid.NewGuid():N}.exe";
    }
}
