using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace WindowsAudioSwitcher.Logging;

/// <summary>
/// Lightweight, thread-safe file logger. Writes one rolling file per day to
/// %APPDATA%\WindowsAudioSwitcher\logs\app-YYYYMMDD.log.
/// </summary>
public static class Logger
{
    private static readonly object _gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsAudioSwitcher", "logs");

    public static string LogFilePath { get; } = Path.Combine(
        LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>How long log files are retained. Older files are deleted at app startup.</summary>
    public static TimeSpan RetentionPeriod { get; } = TimeSpan.FromDays(14);

    static Logger()
    {
        try { Directory.CreateDirectory(LogDirectory); } catch { /* nowhere to log a log failure */ }
        TryCleanOldLogs();
    }

    private static void TryCleanOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now - RetentionPeriod;
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch { /* file in use or permission denied — skip */ }
            }
        }
        catch { /* directory missing, etc. */ }
    }

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static void Banner()
    {
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString()
                      ?? "unknown";
        Info(new string('=', 60));
        Info($"Windows Audio Switcher v{version} starting");
        Info($"PID={Environment.ProcessId}  User={Environment.UserName}  OS={Environment.OSVersion}");
        Info($"Process path: {Environment.ProcessPath}");
        Info(new string('=', 60));
    }

    private static void Write(string level, string message, Exception? ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(level).Append("] ")
          .Append(message);
        if (ex != null)
        {
            sb.AppendLine();
            sb.Append(ex.ToString());
        }
        sb.AppendLine();
        var line = sb.ToString();

        lock (_gate)
        {
            try { File.AppendAllText(LogFilePath, line); }
            catch { /* swallow — logging must never crash the app */ }
        }
        Debug.Write(line);
    }
}
