using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

        // Defense in depth: UpdateChecker already drops untrusted URLs, but this
        // is the method that fetches bytes we are about to execute.
        if (!UpdateUrls.IsTrustedDownload(url))
        {
            Logger.Warn($"UpdateInstaller: refusing to download from untrusted URL '{url}'.");
            throw new InvalidOperationException(
                "The update download link doesn't point at the official GitHub releases. Install it manually from the release page.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "WindowsAudioSwitcher-update");
        Directory.CreateDirectory(tempDir);
        var dest = PrepareDestination(tempDir, SafeNameFromUrl(url));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("WindowsAudioSwitcher", AppVersion.Display));

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        // HttpClient follows GitHub's redirect to its asset CDN. It never
        // downgrades https to http, but check rather than assume.
        if (resp.RequestMessage?.RequestUri is { } finalUri && finalUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Update download was redirected to a non-HTTPS URL ({finalUri.Host}).");

        var total = resp.Content.Headers.ContentLength;
        using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await SaveVerifiedAsync(src, dest, expectedSha256, total, progress, ct).ConfigureAwait(false);
        return dest;
    }

    /// <summary>
    /// Clears out earlier downloads and returns the path to write to. A file
    /// that can't be deleted (an installer from a previous attempt still
    /// running, or a scanner holding it) gets a unique name instead, so it
    /// can't block the new download.
    /// </summary>
    private static string PrepareDestination(string tempDir, string fileName)
    {
        foreach (var old in Directory.EnumerateFiles(tempDir))
        {
            try { File.Delete(old); } catch { /* in use: leave it */ }
        }
        var dest = Path.Combine(tempDir, fileName);
        if (!File.Exists(dest)) return dest;
        return Path.Combine(tempDir,
            $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
    }

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="dest"/>, hashing the
    /// bytes as they stream past, then closes the file and checks the length
    /// and SHA-256. Throws, and deletes the file, if the download is cut short,
    /// cancelled, or doesn't match. On success the file is closed and ready to run.
    /// <para>
    /// Versions 0.5.7 to 0.7.0 kept the download open for writing and then
    /// reopened the file to hash it. Windows refused the second open with a
    /// sharing violation, so every in-app update failed. Hashing while writing
    /// needs no second open, and the file is closed before anything else
    /// touches it.
    /// </para>
    /// </summary>
    internal static async Task SaveVerifiedAsync(
        Stream source,
        string dest,
        string expectedSha256,
        long? expectedLength,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        long received = 0;
        string actual;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                progress?.Report(new DownloadProgress(0, expectedLength));
                while (true)
                {
                    var n = await source.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n == 0) break;
                    hasher.AppendData(buf, 0, n);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    received += n;
                    progress?.Report(new DownloadProgress(received, expectedLength));
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            actual = Convert.ToHexString(hasher.GetHashAndReset()); // uppercase; compared case-insensitively
        }
        catch
        {
            // Cancelled or failed mid-download: never leave a partial installer behind.
            TryDelete(dest);
            throw;
        }

        Logger.Info($"UpdateInstaller: downloaded {received} bytes to {dest}");

        if (expectedLength is long length && received != length)
        {
            TryDelete(dest);
            Logger.Error($"UpdateInstaller: download incomplete ({received} of {length} bytes); deleted it.");
            throw new InvalidOperationException(
                $"The download was incomplete ({received:N0} of {length:N0} bytes), so it was not run. Try again.");
        }

        // Integrity check (mandatory: a missing hash was already rejected).
        // Guards against a corrupted download or a swapped asset. Verified
        // before the file is ever handed to the silent launcher.
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(dest);
            Logger.Error($"UpdateInstaller: SHA-256 mismatch (expected {expectedSha256}, got {actual}); deleted download.");
            throw new InvalidOperationException(
                "The downloaded installer failed its integrity check (SHA-256 mismatch). " +
                "It may be corrupted, so it was not run.");
        }
        Logger.Info("UpdateInstaller: SHA-256 verified.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
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
