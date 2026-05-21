using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using NAudio.CoreAudioApi;
using Wpf.Ui.Appearance;
using WindowsAudioSwitcher.Audio;
using WindowsAudioSwitcher.Logging;
using WindowsAudioSwitcher.Notifications;
using WindowsAudioSwitcher.Rules;
using WindowsAudioSwitcher.Startup;
using WindowsAudioSwitcher.Updates;

namespace WindowsAudioSwitcher;

public partial class App : Application
{
    private const string MutexName = @"Global\WindowsAudioSwitcher.SingleInstance.{8E0B6F8C-9B8E-4F2A-8C7E-1F6C7A1A2B3C}";

    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _tray;
    private AudioDeviceManager? _deviceManager;
    private RuleEngine? _engine;
    private AppSettings _settings = new();
    private MainWindow? _settingsWindow;
    private ToastService? _toasts;
    private bool _startInTray;

    // Last known default device IDs. We toast when these change for ANY reason —
    // our engine, Windows itself, manual user pick — not just when RuleEngine.Apply
    // explicitly issued a SetDefault.
    private string? _lastOutputDefaultId;
    private string? _lastInputDefaultId;

    // Debounce timer for device-event-driven ApplyRules calls. Windows fires
    // 20+ OnDeviceStateChanged events during a single Bluetooth connect; without
    // coalescing, each one runs a full Apply on the UI thread (~800ms apiece),
    // which blocks toast fade animations and the auto-close timer so the user
    // perceives delayed and frozen notifications.
    private DispatcherTimer? _applyDebounce;
    private static readonly TimeSpan ApplyDebounceInterval = TimeSpan.FromMilliseconds(350);

    public event Action<ApplyResult>? RulesApplied;

    /// <summary>Latest release info if a newer version was found at startup. Null until checked.</summary>
    public UpdateInfo? PendingUpdate { get; private set; }
    public event Action<UpdateInfo>? UpdateAvailable;

    public record ApplyResult(
        AudioDevice? Output,
        AudioDevice? Input,
        AudioDevice? CurrentOutput,
        AudioDevice? CurrentInput,
        bool InitialRun,
        string? ErrorMessage);

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        Logger.Banner();
        Logger.Info($"Startup args: [{string.Join(", ", e.Args)}]");

        // Pin the app to dark theme regardless of Windows setting. WPF UI's
        // ApplicationThemeManager applies the resources + tells per-window
        // backdrops (Mica/Acrylic) which palette to use.
        try { ApplicationThemeManager.Apply(ApplicationTheme.Dark); }
        catch (Exception ex) { Logger.Warn($"ApplicationThemeManager.Apply failed: {ex.Message}"); }

        base.OnStartup(e);

        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                Logger.Info("Another instance already owns the mutex — exiting.");
                MessageBox.Show("Windows Audio Switcher is already running (check the system tray).",
                    "Windows Audio Switcher", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            _startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
            Logger.Info($"_startInTray = {_startInTray}");

            _settings = SettingsStore.Load();
            Logger.Info($"Settings loaded from {SettingsStore.SettingsPath}. " +
                        $"OutputPriority={_settings.OutputPriority.Count}, " +
                        $"InputPriority={_settings.InputPriority.Count}, " +
                        $"RunOnStartup={_settings.RunOnStartup}");

            try { StartupRegistration.Sync(_settings.RunOnStartup); }
            catch (Exception ex) { Logger.Error("StartupRegistration.Sync failed", ex); }

            _deviceManager = new AudioDeviceManager(SynchronizationContext.Current);
            Logger.Info("AudioDeviceManager constructed; endpoint notifications registered.");

            _engine = new RuleEngine(_deviceManager);
            _deviceManager.DevicesChanged += OnDevicesChanged;

            _toasts = new ToastService(Dispatcher);

            _applyDebounce = new DispatcherTimer { Interval = ApplyDebounceInterval };
            _applyDebounce.Tick += (_, _) =>
            {
                _applyDebounce!.Stop();
                ApplyRules(initialRun: false);
            };

            CreateTrayIcon();
            Logger.Info("Tray icon created.");

            ApplyRules(initialRun: true);

            if (!_startInTray)
            {
                ShowSettingsWindow();
            }

            // Fire-and-forget update check. UpdateChecker swallows all
            // exceptions internally, so this never crashes startup.
            _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error during OnStartup", ex);
            MessageBox.Show($"Startup failed:\n\n{ex.Message}\n\nSee log:\n{Logger.LogFilePath}",
                "Windows Audio Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var info = await UpdateChecker.CheckAsync().ConfigureAwait(false);
        if (info == null) return;
        Logger.Info($"UpdateCheck: current={info.CurrentVersion}  latest=v{info.LatestVersion}  available={info.IsUpdateAvailable}  prerelease={info.IsPreRelease}");
        if (!info.IsUpdateAvailable || info.IsPreRelease) return;

        PendingUpdate = info;
        // Marshal the notification onto the UI thread so subscribers (MainWindow)
        // can safely touch WPF state.
        Dispatcher.Invoke(() => UpdateAvailable?.Invoke(info));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Dispatcher.UnhandledException", e.Exception);
        e.Handled = true;
    }

    private void OnDevicesChanged()
    {
        // Coalesce bursts of IMM events (a Bluetooth connect can fire 20+ within
        // 2 seconds). The timer restarts on every event and only fires once
        // ApplyDebounceInterval has passed without a new event.
        Logger.Info("DevicesChanged event received (queued for debounce).");
        _applyDebounce?.Stop();
        _applyDebounce?.Start();
    }

    public void ApplyRules(bool initialRun)
    {
        if (_engine == null || _deviceManager == null) return;
        ApplyResult result;
        try
        {
            Logger.Info($"ApplyRules(initialRun={initialRun}) starting.");

            // One COM enumeration per flow. ApplyOne reads from the snapshot and
            // potentially calls SetDefault. Afterwards, re-query just the default
            // IDs (cheap, no enumeration) so the toast diff reflects the new state.
            var outSnap = _deviceManager.Snapshot(DataFlow.Render);
            var inSnap  = _deviceManager.Snapshot(DataFlow.Capture);

            var output = _engine.ApplyOne(_settings.OutputPriority, outSnap);
            var input  = _engine.ApplyOne(_settings.InputPriority,  inSnap);

            Logger.Info($"ApplyRules result: output={(output?.FriendlyName ?? "<no change>")}, " +
                        $"input={(input?.FriendlyName ?? "<no change>")}");

            var curOutId = _deviceManager.TryGetDefaultId(DataFlow.Render);
            var curInId  = _deviceManager.TryGetDefaultId(DataFlow.Capture);
            var curOutput = LookupFromSnapshot(outSnap, curOutId);
            var curInput  = LookupFromSnapshot(inSnap,  curInId);

            // Toast whenever the active default changed since last apply, regardless of
            // who flipped it. On the initial run we just seed the baseline so we don't
            // fire a spurious notification at app startup.
            if (!initialRun)
            {
                if (curOutput != null
                    && !string.Equals(curOutput.Id, _lastOutputDefaultId, StringComparison.OrdinalIgnoreCase))
                {
                    // Toast format matches the "Currently routing" card:
                    // small uppercase label + bold device name. The implicit
                    // verb is "switched" — the toast only appears on change.
                    _toasts?.Show("OUTPUT", curOutput.FriendlyName);
                }
                if (curInput != null
                    && !string.Equals(curInput.Id, _lastInputDefaultId, StringComparison.OrdinalIgnoreCase))
                {
                    _toasts?.Show("INPUT", curInput.FriendlyName);
                }
            }
            _lastOutputDefaultId = curOutput?.Id;
            _lastInputDefaultId  = curInput?.Id;

            result = new ApplyResult(output, input, curOutput, curInput,
                initialRun, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            Logger.Error("ApplyRules failed", ex);
            result = new ApplyResult(null, null, null, null, initialRun, ErrorMessage: ex.Message);
        }

        UpdateTrayTooltip(result.CurrentOutput, result.CurrentInput);
        RulesApplied?.Invoke(result);
    }

    private static AudioDevice? LookupFromSnapshot(DeviceSnapshot snap, string? id)
    {
        if (id == null) return null;
        return snap.Devices.FirstOrDefault(d =>
            string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public AppSettings Settings => _settings;
    public AudioDeviceManager DeviceManager => _deviceManager!;
    public RuleEngine Engine => _engine!;

    public void SaveAndApply(AppSettings updated)
    {
        _settings = updated;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Logger.Error("SettingsStore.Save failed", ex); }
        try { StartupRegistration.Sync(_settings.RunOnStartup); }
        catch (Exception ex) { Logger.Error("StartupRegistration.Sync failed", ex); }
        ApplyRules(initialRun: false);
    }

    private void CreateTrayIcon()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "Windows Audio Switcher",
            ContextMenu = BuildTrayMenu(),
            NoLeftClickDelay = true,
        };
        // Bypass H.NotifyIcon's IconSource async pipeline — in single-file
        // publish it can silently produce a null System.Drawing.Icon and the
        // tray icon then never registers with Windows. UpdateIcon takes a real
        // Icon handle, so Shell_NotifyIcon gets a valid NIF_ICON every time.
        try
        {
            var icon = LoadAppIcon();
            _tray.UpdateIcon(icon);
            // UpdateIcon alone only fills a field — it does NOT issue NIM_ADD.
            // ForceCreate() makes H.NotifyIcon call Shell_NotifyIcon NIM_ADD so
            // the tray entry actually appears in Windows' tray and in
            // Settings → Personalization → Taskbar → Other system tray icons.
            _tray.ForceCreate();
            Logger.Info($"Tray icon applied ({icon.Width}x{icon.Height}) and ForceCreate'd.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load/register tray icon — tray entry will be missing", ex);
        }
        _tray.TrayLeftMouseUp += (_, _) => ShowSettingsWindow();
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => ShowSettingsWindow();

        var applyItem = new MenuItem { Header = "Apply rules now" };
        applyItem.Click += (_, _) => ApplyRules(initialRun: false);

        var logItem = new MenuItem { Header = "Open log folder" };
        logItem.Click += (_, _) => OpenLogFolder();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Shutdown();

        menu.Items.Add(settingsItem);
        menu.Items.Add(applyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(logItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private static void OpenLogFolder()
    {
        try
        {
            if (!Directory.Exists(Logger.LogDirectory))
                Directory.CreateDirectory(Logger.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Logger.LogDirectory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("OpenLogFolder failed", ex);
        }
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new MainWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
    }

    private void UpdateTrayTooltip(AudioDevice? output, AudioDevice? input)
    {
        if (_tray == null) return;
        var lines = new List<string> { "Windows Audio Switcher" };
        if (output != null) lines.Add($"Output: {output.FriendlyName}");
        if (input != null)  lines.Add($"Input: {input.FriendlyName}");
        // NotifyIcon tooltip is capped at 127 chars on Windows.
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 127) text = text.Substring(0, 127);
        try { _tray.ToolTipText = text; } catch { }
    }

    /// <summary>
    /// Loads the app icon as a System.Drawing.Icon (multi-resolution preserved).
    /// Tries the WPF Resource pack URI first, then the manifest resource stream
    /// as a fallback — single-file publish has known issues with the former.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        // Path 1: pack URI (preferred — same path MainWindow.Icon uses).
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var res = Application.GetResourceStream(uri);
            if (res != null)
            {
                using var s = res.Stream;
                Logger.Info("Loaded app.ico via pack URI.");
                return new Icon(s);
            }
            Logger.Warn("Application.GetResourceStream returned null for pack://.../Assets/app.ico — falling back to manifest stream.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Pack URI load failed: {ex.Message} — falling back to manifest stream.");
        }

        // Path 2: manifest resource stream (bypasses Application's resource loader).
        var asm = typeof(App).Assembly;
        const string logicalName = "WindowsAudioSwitcher.Assets.app.ico";
        var stream = asm.GetManifestResourceStream(logicalName);
        if (stream == null)
        {
            var available = string.Join(", ", asm.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"app.ico manifest resource not found (looked for '{logicalName}'). " +
                $"Available: [{available}]");
        }
        using (stream)
        {
            Logger.Info($"Loaded app.ico via manifest stream ({stream.Length} bytes).");
            return new Icon(stream);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info($"OnExit(code={e.ApplicationExitCode})");
        try
        {
            _tray?.Dispose();
            _deviceManager?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception ex) { Logger.Warn($"Cleanup error: {ex.Message}"); }
        base.OnExit(e);
    }
}
