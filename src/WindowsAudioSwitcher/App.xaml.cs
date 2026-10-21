using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using NAudio.CoreAudioApi;
using WindowsAudioSwitcher.Appearance;
using WindowsAudioSwitcher.Audio;
using WindowsAudioSwitcher.Logging;
using WindowsAudioSwitcher.Notifications;
using WindowsAudioSwitcher.Rules;
using WindowsAudioSwitcher.Startup;
using WindowsAudioSwitcher.Updates;

namespace WindowsAudioSwitcher;

public partial class App : Application
{
    private const string InstanceId = "{8E0B6F8C-9B8E-4F2A-8C7E-1F6C7A1A2B3C}";

    // Local\ (per sign-in session), not Global\, so each Windows user on the PC
    // can run their own copy. The suffix isolates a sandboxed dev build.
    private static string MutexName =>
        $@"Local\WindowsAudioSwitcher.SingleInstance.{InstanceId}{AppEnvironment.InstanceSuffix}";

    // A second launch sets this event to ask the running instance to show its window.
    private static string ShowEventName =>
        $@"Local\WindowsAudioSwitcher.ShowSettings.{InstanceId}{AppEnvironment.InstanceSuffix}";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TaskbarIcon? _tray;
    private AudioDeviceManager? _deviceManager;
    private RuleEngine? _engine;
    private AppSettings _settings = new();
    private MainWindow? _settingsWindow;
    private ToastService? _toasts;
    private bool _startInTray;

    // Last known default device IDs. We toast when these change for ANY reason
    // (our engine, Windows itself, a manual pick), not just our own switches.
    private string? _lastOutputDefaultId;
    private string? _lastInputDefaultId;

    // Last failure we toasted per direction, so a switch that keeps failing on
    // every device event produces one toast rather than one per event.
    private readonly Dictionary<DataFlow, string> _lastFailureToast = new();
    private string? _lastGeneralErrorToast;

    // Debounce for device-event-driven applies. Windows fires 20+ endpoint
    // notifications during a single Bluetooth connect; coalesce them.
    private DispatcherTimer? _applyDebounce;
    private static readonly TimeSpan ApplyDebounceInterval = TimeSpan.FromMilliseconds(350);

    // Apply passes run on a worker thread, one at a time. These are only
    // touched on the UI thread.
    private bool _applyInFlight;
    private bool _applyQueued;
    private bool _applyQueuedForce;
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(30);

    // Pause. MaxValue means "until the user resumes".
    private DateTime? _pausedUntilUtc;
    private DispatcherTimer? _pauseTimer;
    private MenuItem? _pauseMenu;
    private MenuItem? _resumeItem;

    // Update checks: at startup, then whenever 24 hours have passed.
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    private DispatcherTimer? _updateTimer;
    // Time of the last SUCCESSFUL check. A failed check (e.g. at sign-in before
    // the network is up) leaves it alone, so the hourly tick retries.
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    // The check in flight, shared by concurrent callers so a tray click during
    // a background check still gets its answer instead of being dropped.
    private Task<UpdateInfo?>? _updateCheck;

    public event Action<ApplyResult>? RulesApplied;
    public event Action? PauseChanged;

    /// <summary>Most recent apply pass. Lets a freshly opened settings window show current state immediately.</summary>
    public ApplyResult? LastResult { get; private set; }

    /// <summary>Latest release info if a newer version was found. Null until one is.</summary>
    public UpdateInfo? PendingUpdate { get; private set; }
    public event Action<UpdateInfo>? UpdateAvailable;

    public sealed record ApplyResult(
        SwitchOutcome Output,
        SwitchOutcome Input,
        AudioDevice? CurrentOutput,
        AudioDevice? CurrentInput,
        DeviceSnapshot OutputSnapshot,
        DeviceSnapshot InputSnapshot,
        bool InitialRun,
        bool Paused,
        string? ErrorMessage);

    // Everything an apply pass needs, captured on the UI thread so the worker
    // never reads state the UI might be mutating.
    private sealed record ApplyRequest(
        List<Rule> OutputRules,
        List<Rule> InputRules,
        bool SwitchCommunications,
        bool Paused,
        bool InitialRun);

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
        if (AppEnvironment.IsSandboxed)
            Logger.Info($"Sandboxed via {AppEnvironment.DataDirVariable}: data in {AppEnvironment.DataDirectory}; Run key untouched.");

        base.OnStartup(e);

        try
        {
            _startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
            Logger.Info($"_startInTray = {_startInTray}");

            _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                // We don't own this mutex, so just drop our handle; OnExit's
                // ReleaseMutex would otherwise throw.
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                if (_startInTray) Logger.Info("Already running; --tray launch exits quietly.");
                else HandOffToRunningInstance();
                Shutdown();
                return;
            }
            ListenForShowRequests();

            var firstRun = !SettingsStore.HasSavedSettings;
            _settings = SettingsStore.Load(out var usedDefaults);
            if (firstRun || usedDefaults) SeedFromInstallerChoices();
            Logger.Info($"Settings loaded from {SettingsStore.SettingsPath}. " +
                        $"OutputPriority={_settings.OutputPriority.Count}, " +
                        $"InputPriority={_settings.InputPriority.Count}, " +
                        $"RunOnStartup={_settings.RunOnStartup}, " +
                        $"SwitchCommunications={_settings.SwitchCommunicationsDevice}, " +
                        $"Theme={_settings.Theme}");

            ThemeService.Initialize(_settings.Theme);

            try { StartupRegistration.Sync(_settings.RunOnStartup); }
            catch (Exception ex) { Logger.Error("StartupRegistration.Sync failed", ex); }

            var deviceManager = new AudioDeviceManager(SynchronizationContext.Current);
            _deviceManager = deviceManager;
            Logger.Info("AudioDeviceManager constructed; endpoint notifications registered.");

            _engine = new RuleEngine((device, includeComms) => deviceManager.SetDefault(device, includeComms));
            deviceManager.DevicesChanged += OnDevicesChanged;

            _toasts = new ToastService(Dispatcher);

            _applyDebounce = new DispatcherTimer { Interval = ApplyDebounceInterval };
            _applyDebounce.Tick += (_, _) =>
            {
                _applyDebounce!.Stop();
                ApplyRules();
            };

            CreateTrayIcon();
            Logger.Info("Tray icon created.");

            ApplyRules(initialRun: true);

            // Show the settings window on launch unless we were started with --tray
            // (sign-in autostart) or the user opted to start minimized to the tray.
            if (!_startInTray && !_settings.StartMinimizedToTray)
            {
                ShowSettingsWindow();
            }

            StartUpdateChecks();
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error during OnStartup", ex);
            MessageBox.Show($"Startup failed:\n\n{ex.Message}\n\nSee log:\n{Logger.LogFilePath}",
                "Windows Audio Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// On a fresh install the installer's "Launch on sign-in" task has already
    /// written the Run key, or deliberately not. Adopt that choice: the
    /// RunOnStartup=true default would otherwise silently re-enable autostart
    /// for a user who unticked it.
    /// </summary>
    private void SeedFromInstallerChoices()
    {
        var registered = !AppEnvironment.IsSandboxed && StartupRegistration.IsRegistered();
        _settings.RunOnStartup = registered;
        Logger.Info($"No saved settings; RunOnStartup seeded from the Run key: {registered}.");
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Logger.Error("Saving seeded settings failed", ex); }
    }

    // ---- Single instance ---------------------------------------------------

    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, timedOut) =>
        {
            if (timedOut) return;
            Logger.Info("Show request received from a second launch.");
            Dispatcher.BeginInvoke(() =>
            {
                try { ShowSettingsWindow(); }
                catch (Exception ex) { Logger.Error("Showing the settings window for a second launch failed", ex); }
            });
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static void HandOffToRunningInstance()
    {
        // Windows only lets a background process take the foreground if the
        // current foreground process (us, just launched by the user) allows it.
        AllowSetForegroundWindow(ASFW_ANY);

        // The running instance may own the mutex but not have created its
        // event yet if both were started at almost the same moment.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
            {
                using (ev) ev.Set();
                Logger.Info("Another instance is running; asked it to show its window.");
                return;
            }
            Thread.Sleep(100);
        }

        Logger.Info("Another instance holds the mutex but isn't listening; showing a notice instead.");
        MessageBox.Show("Windows Audio Switcher is already running (check the system tray).",
            "Windows Audio Switcher", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    // ---- Rule application --------------------------------------------------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Dispatcher.UnhandledException", e.Exception);
        e.Handled = true;
    }

    private void OnDevicesChanged()
    {
        // Coalesce bursts of endpoint events. The timer restarts on every event
        // and only fires once ApplyDebounceInterval passes without a new one.
        Logger.Info("DevicesChanged event received (queued for debounce).");
        _applyDebounce?.Stop();
        _applyDebounce?.Start();
    }

    /// <summary>
    /// Runs one rule pass on a worker thread and reports the result on the UI
    /// thread. Enumeration and switching can take most of a second for
    /// Bluetooth devices, which used to freeze the UI. Calls made while a pass
    /// is running are coalesced into a single follow-up pass.
    /// </summary>
    /// <param name="force">Apply even while automatic switching is paused (an explicit user request).</param>
    public async void ApplyRules(bool initialRun = false, bool force = false)
    {
        if (_engine == null || _deviceManager == null) return;
        if (_applyInFlight)
        {
            _applyQueued = true;
            _applyQueuedForce |= force;
            return;
        }

        _applyInFlight = true;
        try
        {
            var nextInitial = initialRun;
            var nextForce = force;
            while (true)
            {
                var request = CaptureRequest(nextInitial, nextForce);
                ApplyResult result;
                try
                {
                    var work = Task.Run(() => ApplyCore(request));
                    if (await Task.WhenAny(work, Task.Delay(ApplyTimeout)) == work)
                    {
                        result = await work;
                    }
                    else
                    {
                        // A COM call into the audio service never returned (a wedged
                        // Bluetooth driver can do this). Abandon that pass so later
                        // device events and rule edits still get a pass of their own,
                        // instead of queueing behind it forever.
                        Logger.Error($"Rule pass did not finish within {ApplyTimeout.TotalSeconds:F0} s; abandoning it. " +
                                     "The Windows audio service may be unresponsive.");
                        _ = work.ContinueWith(t => Logger.Warn(
                                $"Abandoned rule pass finished late ({t.Status}): {t.Exception?.GetBaseException().Message}"),
                            TaskScheduler.Default);
                        result = new ApplyResult(SwitchOutcome.None, SwitchOutcome.None, null, null,
                            DeviceSnapshot.Empty, DeviceSnapshot.Empty, request.InitialRun, request.Paused,
                            $"Windows audio didn't respond within {ApplyTimeout.TotalSeconds:F0} seconds.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("ApplyRules failed", ex);
                    result = new ApplyResult(SwitchOutcome.None, SwitchOutcome.None, null, null,
                        DeviceSnapshot.Empty, DeviceSnapshot.Empty, request.InitialRun, request.Paused, ex.Message);
                }

                try { OnApplyCompleted(result); }
                catch (Exception ex) { Logger.Error("Reporting apply result failed", ex); }

                if (!_applyQueued) break;
                _applyQueued = false;
                nextInitial = false;
                nextForce = _applyQueuedForce;
                _applyQueuedForce = false;
            }
        }
        finally
        {
            _applyInFlight = false;
        }
    }

    private ApplyRequest CaptureRequest(bool initialRun, bool force) => new(
        _settings.OutputPriority.Select(r => r.Clone()).ToList(),
        _settings.InputPriority.Select(r => r.Clone()).ToList(),
        _settings.SwitchCommunicationsDevice,
        Paused: IsPaused && !force,
        InitialRun: initialRun);

    // Worker thread. Touches only the request, the device manager and the engine.
    private ApplyResult ApplyCore(ApplyRequest req)
    {
        var dm = _deviceManager!;
        var engine = _engine!;
        Logger.Info($"ApplyRules(initialRun={req.InitialRun}, paused={req.Paused}) starting.");

        var outSnap = dm.Snapshot(DataFlow.Render, req.SwitchCommunications);
        var inSnap  = dm.Snapshot(DataFlow.Capture, req.SwitchCommunications);

        SwitchOutcome output, input;
        if (req.Paused)
        {
            output = new SwitchOutcome(RuleEngine.PickTarget(req.OutputRules, outSnap), false, null);
            input  = new SwitchOutcome(RuleEngine.PickTarget(req.InputRules, inSnap), false, null);
        }
        else
        {
            output = engine.ApplyOne(req.OutputRules, outSnap, req.SwitchCommunications);
            input  = engine.ApplyOne(req.InputRules,  inSnap,  req.SwitchCommunications);
        }

        Logger.Info($"ApplyRules result: output={Describe(output)}, input={Describe(input)}");

        // After any switch attempt, re-read just the default IDs (cheap, no
        // enumeration) so the toast diff and the window reflect the real state.
        // A failed attempt can still have moved some roles: PolicyConfig tries
        // every role and only then reports the ones that failed.
        if (output.Switched || output.Error != null) outSnap = outSnap with { CurrentDefaultId = dm.TryGetDefaultId(DataFlow.Render) };
        if (input.Switched  || input.Error  != null) inSnap  = inSnap  with { CurrentDefaultId = dm.TryGetDefaultId(DataFlow.Capture) };

        return new ApplyResult(output, input,
            outSnap.Find(outSnap.CurrentDefaultId), inSnap.Find(inSnap.CurrentDefaultId),
            outSnap, inSnap, req.InitialRun, req.Paused, ErrorMessage: null);
    }

    private static string Describe(SwitchOutcome o) =>
        o.Error != null ? $"FAILED to switch to {o.Target?.FriendlyName}: {o.Error}"
        : o.Switched    ? $"switched to {o.Target?.FriendlyName}"
        : o.Target != null ? $"<no change, {o.Target.FriendlyName}>"
        : "<no matching rule>";

    // UI thread.
    private void OnApplyCompleted(ApplyResult result)
    {
        // Toast whenever the active default changed since the last pass,
        // regardless of who flipped it. The initial pass only seeds the baseline
        // so starting the app doesn't produce a notification.
        if (!result.InitialRun && result.ErrorMessage == null)
        {
            if (result.CurrentOutput != null && !RuleEngine.IdEquals(result.CurrentOutput.Id, _lastOutputDefaultId))
                _toasts?.Show("OUTPUT", result.CurrentOutput.FriendlyName);
            if (result.CurrentInput != null && !RuleEngine.IdEquals(result.CurrentInput.Id, _lastInputDefaultId))
                _toasts?.Show("INPUT", result.CurrentInput.FriendlyName);
        }
        if (result.ErrorMessage == null)
        {
            _lastOutputDefaultId = result.CurrentOutput?.Id;
            _lastInputDefaultId  = result.CurrentInput?.Id;
        }

        ToastSwitchFailure(DataFlow.Render, "OUTPUT", result.Output);
        ToastSwitchFailure(DataFlow.Capture, "INPUT", result.Input);

        if (result.ErrorMessage != null && result.ErrorMessage != _lastGeneralErrorToast)
        {
            _toasts?.Show("COULDN'T APPLY RULES", result.ErrorMessage, ToastKind.Error, onClick: () => ShowSettingsWindow());
        }
        _lastGeneralErrorToast = result.ErrorMessage;

        LastResult = result;
        UpdateTrayTooltip();
        RulesApplied?.Invoke(result);
    }

    private void ToastSwitchFailure(DataFlow flow, string label, SwitchOutcome outcome)
    {
        if (outcome.Error == null || outcome.Target == null)
        {
            _lastFailureToast.Remove(flow);
            return;
        }
        var key = outcome.Target.Id + "|" + outcome.Error;
        if (_lastFailureToast.TryGetValue(flow, out var last) && last == key) return;
        _lastFailureToast[flow] = key;
        // The settings window is usually closed, so a toast is the only place
        // a failed switch is visible. Clicking it opens the window with details.
        _toasts?.Show($"COULDN'T SWITCH {label}", $"{outcome.Target.FriendlyName}: {outcome.Error}",
            ToastKind.Error, onClick: () => ShowSettingsWindow());
    }

    public AppSettings Settings => _settings;
    public AudioDeviceManager DeviceManager => _deviceManager!;

    public void SaveAndApply(AppSettings updated)
    {
        _settings = updated;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Logger.Error("SettingsStore.Save failed", ex); }
        try { StartupRegistration.Sync(_settings.RunOnStartup); }
        catch (Exception ex) { Logger.Error("StartupRegistration.Sync failed", ex); }
        ApplyRules();
    }

    /// <summary>
    /// Persists settings WITHOUT re-running the rule engine. Used to flush a
    /// pending debounced edit on app exit: we want the user's last change saved,
    /// but switching the default device as a side effect of shutting down would
    /// be surprising.
    /// </summary>
    public void SaveSettingsOnly(AppSettings updated)
    {
        _settings = updated;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Logger.Error("SettingsStore.Save (exit flush) failed", ex); }
        try { StartupRegistration.Sync(_settings.RunOnStartup); }
        catch (Exception ex) { Logger.Error("StartupRegistration.Sync failed", ex); }
    }

    // ---- Pause -------------------------------------------------------------

    public bool IsPaused => _pausedUntilUtc is { } until && DateTime.UtcNow < until;

    /// <summary>"Paused until 14:30", "Paused until you resume", or null when not paused.</summary>
    public string? PauseDescription => !IsPaused
        ? null
        : _pausedUntilUtc == DateTime.MaxValue
            ? "Paused until you resume"
            : $"Paused until {_pausedUntilUtc!.Value.ToLocalTime():t}";

    /// <param name="duration">How long to pause, or null to pause until resumed.</param>
    public void PauseSwitching(TimeSpan? duration)
    {
        _pausedUntilUtc = duration is { } d ? DateTime.UtcNow + d : DateTime.MaxValue;
        Logger.Info($"Automatic switching paused ({PauseDescription}).");

        // Poll the wall clock instead of one long timer, so a laptop that slept
        // past the end of the pause resumes promptly after waking.
        _pauseTimer ??= CreatePauseTimer();
        if (duration != null) _pauseTimer.Start();
        else _pauseTimer.Stop();

        OnPauseStateChanged();
    }

    private DispatcherTimer CreatePauseTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => { if (!IsPaused) ResumeSwitching(); };
        return timer;
    }

    public void ResumeSwitching()
    {
        if (_pausedUntilUtc == null) return;
        _pausedUntilUtc = null;
        _pauseTimer?.Stop();
        Logger.Info("Automatic switching resumed.");
        OnPauseStateChanged();
        // Catch up on whatever changed while paused.
        ApplyRules();
    }

    private void OnPauseStateChanged()
    {
        UpdateTrayMenuState();
        UpdateTrayTooltip();
        PauseChanged?.Invoke();
    }

    // ---- Updates -----------------------------------------------------------

    private void StartUpdateChecks()
    {
        _ = CheckForUpdatesAsync(userInitiated: false);

        // Tick hourly and compare wall-clock time, so a PC that slept through
        // the 24-hour mark checks soon after waking rather than a day later.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _updateTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lastUpdateCheckUtc >= UpdateCheckInterval)
                _ = CheckForUpdatesAsync(userInitiated: false);
        };
        _updateTimer.Start();
    }

    /// <param name="userInitiated">
    /// True for the tray's "Check for updates": always gives feedback, even
    /// when there's nothing new or the check failed.
    /// </param>
    public async Task CheckForUpdatesAsync(bool userInitiated)
    {
        _updateCheck ??= RunUpdateCheckAsync();
        UpdateInfo? info;
        try
        {
            info = await _updateCheck;
        }
        finally
        {
            _updateCheck = null;
        }

        if (info == null)
        {
            if (userInitiated)
                _toasts?.Show("UPDATES", "Couldn't check for updates. Click to open the log.",
                    ToastKind.Error, onClick: OpenLogFolder);
            return;
        }

        Logger.Info($"UpdateCheck: current={info.CurrentVersion}  latest={info.LatestVersion}  " +
                    $"available={info.IsUpdateAvailable}  prerelease={info.IsPreRelease}");
        if (!info.IsUpdateAvailable || info.IsPreRelease)
        {
            if (userInitiated)
                _toasts?.Show("UP TO DATE", $"Windows Audio Switcher v{AppVersion.Display}", ToastKind.Info);
            return;
        }

        var isNew = PendingUpdate == null || PendingUpdate.LatestVersion != info.LatestVersion;
        PendingUpdate = info;
        if (isNew || userInitiated) UpdateAvailable?.Invoke(info);

        if (userInitiated)
        {
            ShowSettingsWindow();
        }
        else if (isNew && _settingsWindow?.IsVisible != true)
        {
            // A tray app can run for weeks; without this the update banner
            // would only be seen by someone who happens to open Settings.
            _toasts?.Show("UPDATE AVAILABLE", $"Version {info.LatestVersion} is ready. Click to update.",
                ToastKind.Info, TimeSpan.FromSeconds(8), () => ShowSettingsWindow());
        }
    }

    private async Task<UpdateInfo?> RunUpdateCheckAsync()
    {
        // UpdateChecker never throws. Resumes on the UI thread.
        var info = await UpdateChecker.CheckAsync();
        if (info != null) _lastUpdateCheckUtc = DateTime.UtcNow;
        return info;
    }

    // ---- Tray --------------------------------------------------------------

    private void CreateTrayIcon()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "Windows Audio Switcher",
            ContextMenu = BuildTrayMenu(),
            NoLeftClickDelay = true,
        };
        // Bypass H.NotifyIcon's IconSource async pipeline: in single-file
        // publish it can silently produce a null System.Drawing.Icon and the
        // tray icon then never registers with Windows. UpdateIcon takes a real
        // Icon handle, so Shell_NotifyIcon gets a valid NIF_ICON every time.
        try
        {
            var icon = LoadAppIcon();
            _tray.UpdateIcon(icon);
            // UpdateIcon alone only fills a field; it does NOT issue NIM_ADD.
            // ForceCreate() makes H.NotifyIcon call Shell_NotifyIcon NIM_ADD so
            // the tray entry actually appears.
            _tray.ForceCreate();
            Logger.Info($"Tray icon applied ({icon.Width}x{icon.Height}) and ForceCreate'd.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load/register tray icon; tray entry will be missing", ex);
        }
        _tray.TrayLeftMouseUp += (_, _) => ShowSettingsWindow();
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuEntry("Settings…", () => ShowSettingsWindow()));
        menu.Items.Add(MenuEntry("Apply rules now", () => ApplyRules(force: true)));
        menu.Items.Add(new Separator());

        _pauseMenu = new MenuItem { Header = "Pause automatic switching" };
        _pauseMenu.Items.Add(MenuEntry("For 15 minutes", () => PauseSwitching(TimeSpan.FromMinutes(15))));
        _pauseMenu.Items.Add(MenuEntry("For 1 hour", () => PauseSwitching(TimeSpan.FromHours(1))));
        _pauseMenu.Items.Add(MenuEntry("Until I resume", () => PauseSwitching(null)));
        _resumeItem = MenuEntry("Resume automatic switching", ResumeSwitching);
        _resumeItem.Visibility = Visibility.Collapsed;
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new Separator());

        menu.Items.Add(MenuEntry("Check for updates", () => _ = CheckForUpdatesAsync(userInitiated: true)));
        menu.Items.Add(MenuEntry("Open log folder", OpenLogFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Exit", () => Shutdown()));
        return menu;
    }

    private static MenuItem MenuEntry(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void UpdateTrayMenuState()
    {
        if (_pauseMenu == null || _resumeItem == null) return;
        var paused = IsPaused;
        _pauseMenu.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        _resumeItem.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        _resumeItem.Header = paused ? $"Resume automatic switching ({PauseDescription!.ToLowerInvariant()})" : "Resume automatic switching";
    }

    private void UpdateTrayTooltip()
    {
        if (_tray == null) return;
        var lines = new List<string> { "Windows Audio Switcher" };
        if (PauseDescription is { } pause) lines.Add(pause);
        if (LastResult?.CurrentOutput is { } output) lines.Add($"Output: {output.FriendlyName}");
        if (LastResult?.CurrentInput is { } input) lines.Add($"Input: {input.FriendlyName}");
        // NotifyIcon tooltips are capped at 127 chars on Windows.
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 127) text = text.Substring(0, 127);
        try { _tray.ToolTipText = text; } catch { }
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

    /// <summary>
    /// Loads the app icon as a System.Drawing.Icon (multi-resolution preserved).
    /// Tries the WPF Resource pack URI first, then the manifest resource stream
    /// as a fallback: single-file publish has known issues with the former.
    /// </summary>
    private static Icon LoadAppIcon()
    {
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
            Logger.Warn("Application.GetResourceStream returned null for pack://.../Assets/app.ico; falling back to manifest stream.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Pack URI load failed: {ex.Message}; falling back to manifest stream.");
        }

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
        // A debounced edit from the last ~500 ms is flushed by the settings
        // window's own Closed handler: WPF closes every window before OnExit,
        // so _settingsWindow is already null here.
        try
        {
            _applyDebounce?.Stop();
            _pauseTimer?.Stop();
            _updateTimer?.Stop();
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            _tray?.Dispose();
            _deviceManager?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception ex) { Logger.Warn($"Cleanup error: {ex.Message}"); }
        base.OnExit(e);
    }
}
