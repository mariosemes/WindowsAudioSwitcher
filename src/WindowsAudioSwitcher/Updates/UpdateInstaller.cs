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
/// mid-install; /RESTARTAPPLICATIONS brings it back at the new version.
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
    /// path. Reports byte counts to <paramref name="progress"/>. Caller decides
    /// when to launch the installer.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string url,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
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
        return dest;
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
            // Inno Setup silent install. SUPPRESSMSGBOXES auto-answers any
            // upgrade prompts. RESTARTAPPLICATIONS re-launches the app the
            // installer closed (us) once install finishes.
            Arguments = "/SILENT /SUPPRESSMSGBOXES /RESTARTAPPLICATIONS",
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
