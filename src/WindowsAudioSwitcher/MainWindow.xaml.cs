using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Wpf.Ui.Controls;
using WindowsAudioSwitcher.Appearance;
using WindowsAudioSwitcher.Audio;
using WindowsAudioSwitcher.Logging;
using WindowsAudioSwitcher.Rules;
using WindowsAudioSwitcher.Updates;

namespace WindowsAudioSwitcher;

public partial class MainWindow : FluentWindow
{
    private readonly ObservableCollection<Rule> _outRules = new();
    private readonly ObservableCollection<Rule> _inRules = new();
    private DispatcherTimer? _saveDebounce;
    private bool _loading;
    // True while a drag-and-drop is in flight. Move() fires on every DragOver
    // tick; this defers the save+apply until the drop so a reorder is committed
    // (and the rules re-evaluated) once rather than dozens of times.
    private bool _isDragging;

    // Latest device snapshots from the rule engine, used to colour each rule's
    // status dot and to list devices in the add/edit dialog without another
    // COM enumeration on the UI thread.
    private DeviceSnapshot? _outSnapshot;
    private DeviceSnapshot? _inSnapshot;

    // Captured Y of each visible ListBoxItem just BEFORE we mutate the rules
    // collection during a reorder. After the next layout pass we use these to set
    // each row's TranslateTransform.Y to (old - new) and animate it back to 0,
    // which produces the slide-into-place feel during live reorder.
    private readonly Dictionary<ListBoxItem, double> _capturedY = new();
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly IEasingFunction SlideEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    public MainWindow()
    {
        InitializeComponent();
        // Stamp the running app's version into the title bar. The visible chrome
        // shows it as a dimmed label next to the title; the OS-level window title
        // (taskbar / alt-tab) gets the combined string since it can't be styled.
        var versionLabel = $"v{AppVersion.Display}";
        Title = $"Windows Audio Switcher  {versionLabel}";
        AppTitleBarVersion.Text = versionLabel;

        OutList.ItemsSource = _outRules;
        InList.ItemsSource = _inRules;
        _outRules.CollectionChanged += OnRulesCollectionChanged;
        _inRules.CollectionChanged += OnRulesCollectionChanged;
        LoadFromSettings();
        RestoreWindowBounds();
        UpdateEmptyStates();

        // Show the state from the last rule pass right away, including a
        // failure (the window is often opened by clicking an error toast).
        // Later passes arrive through RulesApplied.
        if (TheApp.LastResult is { } last)
        {
            ShowDeviceState(last);
            ShowFailures(last);
        }
        UpdatePauseBanner();

        TheApp.RulesApplied += OnRulesApplied;
        TheApp.UpdateAvailable += OnUpdateAvailable;
        TheApp.PauseChanged += UpdatePauseBanner;
        // If the update check finished BEFORE this window was opened, surface it now.
        if (TheApp.PendingUpdate != null) OnUpdateAvailable(TheApp.PendingUpdate);
        Closed += (_, _) =>
        {
            // The window only really closes when the app exits (a normal close
            // just hides it), and WPF closes windows before App.OnExit runs.
            // Persist a debounced edit from the last 500 ms now, or it is lost.
            try { FlushPendingSave(); }
            catch (Exception ex) { Logger.Warn($"FlushPendingSave on close failed: {ex.Message}"); }
            TheApp.RulesApplied -= OnRulesApplied;
            TheApp.UpdateAvailable -= OnUpdateAvailable;
            TheApp.PauseChanged -= UpdatePauseBanner;
        };
    }

    private App TheApp => (App)Application.Current;

    // ---- Update banner -----------------------------------------------------

    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _downloadCts;

    private void OnUpdateAvailable(UpdateInfo info)
    {
        // Don't reset the banner under a download that's already running.
        if (_downloadCts != null) return;
        _pendingUpdate = info;
        UpdateBannerTitle.Text   = $"Update available: v{info.LatestVersion}";
        var sizeNote = info.InstallerSizeBytes is long bytes
            ? $"  ·  {bytes / 1024 / 1024} MB"
            : string.Empty;
        UpdateBannerMessage.Text = $"You're on v{info.CurrentVersion}. Click Update to install v{info.LatestVersion}{sizeNote}.";
        UpdateBanner.Visibility = Visibility.Visible;
        SetUpdateBannerIdleState();
    }

    private void SetUpdateBannerIdleState()
    {
        UpdateInstallBtn.Visibility = Visibility.Visible;
        UpdateInstallBtn.IsEnabled  = true;
        UpdateCancelBtn.Visibility  = Visibility.Collapsed;
        UpdateDismissBtn.Visibility = Visibility.Visible;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
    }

    private void SetUpdateBannerDownloadingState()
    {
        UpdateInstallBtn.IsEnabled  = false;
        UpdateInstallBtn.Visibility = Visibility.Collapsed;
        UpdateCancelBtn.Visibility  = Visibility.Visible;
        UpdateDismissBtn.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
    }

    private async void UpdateInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        var update = _pendingUpdate;
        var url = update.InstallerUrl;
        if (string.IsNullOrEmpty(url))
        {
            // No installer attached to the release: fall back to the release page.
            OpenReleasePage(update.ReleaseUrl);
            return;
        }

        SetUpdateBannerDownloadingState();
        _downloadCts = new CancellationTokenSource();
        var versionLabel = $"v{update.LatestVersion}";
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Percent is double pct)
            {
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = pct;
                var receivedMb = p.BytesReceived / 1024d / 1024d;
                var totalMb    = (p.TotalBytes ?? 0) / 1024d / 1024d;
                UpdateBannerMessage.Text = $"Downloading {versionLabel}: {pct:F0}%  ({receivedMb:F1} / {totalMb:F1} MB)";
            }
            else
            {
                UpdateProgressBar.IsIndeterminate = true;
                var receivedMb = p.BytesReceived / 1024d / 1024d;
                UpdateBannerMessage.Text = $"Downloading {versionLabel}: {receivedMb:F1} MB";
            }
        });

        try
        {
            var path = await UpdateInstaller.DownloadAsync(url, update.ExpectedSha256, progress, _downloadCts.Token);
            UpdateBannerMessage.Text = $"Launching installer for {versionLabel}…";
            UpdateProgressBar.IsIndeterminate = true;
            // Hand off to Inno Setup. CloseApplications=force will terminate this
            // process if Shutdown doesn't beat the installer's first file write.
            UpdateInstaller.LaunchInstallerAndExit(path);
        }
        catch (OperationCanceledException)
        {
            UpdateBannerMessage.Text = "Download cancelled. Click Update to try again.";
            SetUpdateBannerIdleState();
        }
        catch (Exception ex)
        {
            UpdateBannerMessage.Text = $"Update failed: {ex.Message} Opening the release page.";
            SetUpdateBannerIdleState();
            OpenReleasePage(update.ReleaseUrl);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void UpdateCancelBtn_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void UpdateDismissBtn_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private static void OpenReleasePage(string url)
    {
        // The URL came from the GitHub API response; only ever hand our own
        // release pages to the shell.
        if (!UpdateUrls.IsTrustedReleasePage(url))
        {
            Logger.Warn($"Not opening untrusted release URL '{url}'.");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Opening release page failed: {ex.Message}");
        }
    }

    // ---- Pause banner ------------------------------------------------------

    private void UpdatePauseBanner()
    {
        var description = TheApp.PauseDescription;
        PauseBanner.Visibility = description == null ? Visibility.Collapsed : Visibility.Visible;
        if (description != null) PauseBannerTitle.Text = $"Automatic switching is paused ({description.ToLowerInvariant()})";
    }

    private void ResumeBtn_Click(object sender, RoutedEventArgs e) => TheApp.ResumeSwitching();

    // ---- Window lifetime ---------------------------------------------------

    /// <summary>Intercept the X / Alt+F4 close so the app keeps running in the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowBounds();
        // Closing the window only hides it; the app lives on in the tray. The
        // process is torn down through App.OnExit (tray "Exit" / auto-update),
        // which never routes through here.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            foreach (var r in _outRules) r.PropertyChanged -= OnRulePropertyChanged;
            foreach (var r in _inRules)  r.PropertyChanged -= OnRulePropertyChanged;
            _outRules.Clear();
            _inRules.Clear();

            foreach (var r in TheApp.Settings.OutputPriority)
            {
                r.PropertyChanged += OnRulePropertyChanged;
                _outRules.Add(r);
            }
            foreach (var r in TheApp.Settings.InputPriority)
            {
                r.PropertyChanged += OnRulePropertyChanged;
                _inRules.Add(r);
            }
            RunOnStartupCheck.IsChecked   = TheApp.Settings.RunOnStartup;
            StartMinimizedCheck.IsChecked = TheApp.Settings.StartMinimizedToTray;
            SwitchCommsCheck.IsChecked    = TheApp.Settings.SwitchCommunicationsDevice;
            ThemeCombo.SelectedItem = ThemeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string)i.Tag == TheApp.Settings.Theme.ToString())
                ?? ThemeCombo.Items[0];
        }
        finally { _loading = false; }
    }

    private void RestoreWindowBounds()
    {
        var s = TheApp.Settings;
        if (s.WindowLeft is double left && s.WindowTop is double top
            && s.WindowWidth is double w && s.WindowHeight is double h
            && w >= MinWidth && h >= MinHeight)
        {
            var screenLeft   = SystemParameters.VirtualScreenLeft;
            var screenRight  = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            var screenBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (left + 100 >= screenLeft && left <= screenRight - 100 &&
                top  + 50  >= SystemParameters.VirtualScreenTop && top <= screenBottom - 50)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left   = left;
                Top    = top;
                Width  = w;
                Height = h;
            }
        }
    }

    private void SaveWindowBounds()
    {
        var b = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        TheApp.Settings.WindowLeft   = b.Left;
        TheApp.Settings.WindowTop    = b.Top;
        TheApp.Settings.WindowWidth  = b.Width;
        TheApp.Settings.WindowHeight = b.Height;
        try { SettingsStore.Save(TheApp.Settings); } catch { /* bounds are a UX nicety */ }
    }

    // ---- Device state and rule status --------------------------------------

    private void OnRulesApplied(App.ApplyResult result)
    {
        ShowDeviceState(result);

        if (ShowFailures(result)) return;
        // While paused the banner already says nothing will switch; a status
        // line on every device-event pass would just repeat it.
        if (result.InitialRun || result.Paused) return;

        var parts = new List<string>();
        if (result.Output.Switched) parts.Add($"Output → {result.Output.Target!.FriendlyName}");
        if (result.Input.Switched)  parts.Add($"Input → {result.Input.Target!.FriendlyName}");
        if (parts.Count == 0)
            ShowStatus("Applied. No change needed.", InfoBarSeverity.Informational);
        else
            ShowStatus($"Applied: {string.Join("  ·  ", parts)}", InfoBarSeverity.Success);
    }

    /// <summary>Shows a failed pass or failed switch in the InfoBar. Returns true if there was one.</summary>
    private bool ShowFailures(App.ApplyResult result)
    {
        if (result.ErrorMessage != null)
        {
            ShowStatus($"Apply failed: {result.ErrorMessage}", InfoBarSeverity.Error);
            return true;
        }
        var failures = new List<string>();
        if (result.Output.Error != null) failures.Add($"Couldn't switch output to {result.Output.Target?.FriendlyName}: {result.Output.Error}");
        if (result.Input.Error != null)  failures.Add($"Couldn't switch input to {result.Input.Target?.FriendlyName}: {result.Input.Error}");
        if (failures.Count == 0) return false;
        ShowStatus(string.Join("  ", failures), InfoBarSeverity.Error);
        return true;
    }

    private void ShowDeviceState(App.ApplyResult result)
    {
        if (result.ErrorMessage == null)
        {
            _outSnapshot = result.OutputSnapshot;
            _inSnapshot  = result.InputSnapshot;
        }
        OutCurrentText.Text = result.CurrentOutput?.FriendlyName ?? "(none)";
        InCurrentText.Text  = result.CurrentInput?.FriendlyName  ?? "(none)";
        UpdateRuleStatuses();
    }

    private void UpdateRuleStatuses()
    {
        ApplyStatuses(_outRules, _outSnapshot);
        ApplyStatuses(_inRules, _inSnapshot);
    }

    private static void ApplyStatuses(IList<Rule> rules, DeviceSnapshot? snapshot)
    {
        var statuses = RuleEngine.ComputeStatuses(rules, snapshot);
        for (var i = 0; i < rules.Count; i++)
        {
            var match = statuses[i];
            var names = match.Status == RuleStatus.Active && match.Picked != null
                ? match.Picked.FriendlyName
                : string.Join(", ", match.Matches.Select(d => d.FriendlyName));
            rules[i].SetStatus(match.Status, names);
        }
    }

    // ---- Add / edit / remove ----------------------------------------------

    private void OutAddBtn_Click(object sender, RoutedEventArgs e) =>
        OpenAddRuleDialog(_outRules, DataFlow.Render, "Add output rule");

    private void InAddBtn_Click(object sender, RoutedEventArgs e) =>
        OpenAddRuleDialog(_inRules, DataFlow.Capture, "Add input rule");

    private bool _dialogOpening;

    /// <summary>
    /// Fresh list of connected devices for the add/edit dialog, so a device
    /// plugged in a moment ago is included. Enumerated on a worker thread
    /// because Bluetooth endpoints can take most of a second to read.
    /// </summary>
    private async Task<IReadOnlyList<AudioDevice>?> ConnectedDevicesAsync(DataFlow flow)
    {
        if (_dialogOpening) return null;   // ignore a double click while enumerating
        _dialogOpening = true;
        var previousCursor = Cursor;
        Cursor = Cursors.AppStarting;
        try
        {
            var manager = TheApp.DeviceManager;
            return (await Task.Run(() => manager.Snapshot(flow))).Devices;
        }
        finally
        {
            Cursor = previousCursor;
            _dialogOpening = false;
        }
    }

    private async void OpenAddRuleDialog(ObservableCollection<Rule> target, DataFlow flow, string title)
    {
        if (await ConnectedDevicesAsync(flow) is not { } devices) return;
        var dlg = new AddRuleDialog(title, devices) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } rule) return;

        rule.PropertyChanged += OnRulePropertyChanged;
        target.Add(rule);

        var message = rule.Kind == RuleKind.ExactDevice
            ? $"Added device rule: {rule.Label}"
            : $"Added name rule: \"{rule.Value}\"";
        ShowStatus(message, InfoBarSeverity.Informational);
    }

    private void RuleRemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Rule rule) return;
        if (_outRules.Contains(rule)) RemoveRule(_outRules, rule);
        else if (_inRules.Contains(rule)) RemoveRule(_inRules, rule);
    }

    private async void RuleEditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Rule rule) return;

        ObservableCollection<Rule> list;
        DataFlow flow;
        string title;
        if (_outRules.Contains(rule))      { list = _outRules; flow = DataFlow.Render;  title = "Edit output rule"; }
        else if (_inRules.Contains(rule))  { list = _inRules;  flow = DataFlow.Capture; title = "Edit input rule"; }
        else return;

        if (await ConnectedDevicesAsync(flow) is not { } devices) return;
        var dlg = new AddRuleDialog(title, devices, rule) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } newRule) return;

        var index = list.IndexOf(rule);
        if (index < 0) return;

        // Preserve on/off state across the swap: the user is editing the
        // match criteria, not toggling the rule on or off.
        newRule.IsEnabled = rule.IsEnabled;

        rule.PropertyChanged -= OnRulePropertyChanged;
        newRule.PropertyChanged += OnRulePropertyChanged;
        list[index] = newRule;

        var message = newRule.Kind == RuleKind.ExactDevice
            ? $"Updated device rule: {newRule.Label}"
            : $"Updated name rule: \"{newRule.Value}\"";
        ShowStatus(message, InfoBarSeverity.Informational);
    }

    private void RemoveRule(ObservableCollection<Rule> list, Rule rule)
    {
        rule.PropertyChanged -= OnRulePropertyChanged;
        list.Remove(rule);
    }

    // Explicit request: runs even while automatic switching is paused.
    private void ApplyNowBtn_Click(object sender, RoutedEventArgs e) => TheApp.ApplyRules(force: true);

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

    private void SettingsToggle_Changed(object sender, RoutedEventArgs e) => ScheduleSave();

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // Apply immediately for live feedback; persistence goes through the
        // normal debounced save.
        ThemeService.Apply(SelectedTheme());
        ScheduleSave();
    }

    private AppTheme SelectedTheme() =>
        ThemeCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<AppTheme>(tag, out var theme)
            ? theme
            : AppTheme.Dark;

    // ---- Auto-save plumbing -------------------------------------------------
    //
    // We persist+apply on a 500 ms debounce after any mutation, so the user
    // doesn't need an explicit "Save" button. "Apply now" stays for re-running
    // the engine without any rule edit.

    private void OnRulesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyStates();
        UpdateRuleStatuses();
        // During a drag, Move() fires repeatedly. Skip the save+apply path;
        // we commit once in the drag's finally block.
        if (_isDragging) return;
        ScheduleSave();
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Rule.IsEnabled)) return;
        UpdateRuleStatuses();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_loading) return;
        if (_saveDebounce == null)
        {
            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveDebounce.Tick += (_, _) =>
            {
                _saveDebounce!.Stop();
                CommitSettings();
            };
        }
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private AppSettings BuildSettingsFromUi() => new()
    {
        OutputPriority             = _outRules.ToList(),
        InputPriority              = _inRules.ToList(),
        RunOnStartup               = RunOnStartupCheck.IsChecked == true,
        StartMinimizedToTray       = StartMinimizedCheck.IsChecked == true,
        SwitchCommunicationsDevice = SwitchCommsCheck.IsChecked == true,
        Theme                      = SelectedTheme(),
        WindowLeft   = TheApp.Settings.WindowLeft,
        WindowTop    = TheApp.Settings.WindowTop,
        WindowWidth  = TheApp.Settings.WindowWidth,
        WindowHeight = TheApp.Settings.WindowHeight,
    };

    private void CommitSettings() => TheApp.SaveAndApply(BuildSettingsFromUi());

    /// <summary>
    /// If a debounced save is still pending (the user edited within the last
    /// 500 ms), persist it now WITHOUT re-applying rules. Called from App.OnExit
    /// so a last-second edit isn't lost when the debounce timer never fires.
    /// </summary>
    public void FlushPendingSave()
    {
        if (_saveDebounce is { IsEnabled: true })
        {
            _saveDebounce.Stop();
            TheApp.SaveSettingsOnly(BuildSettingsFromUi());
        }
    }

    private void UpdateEmptyStates()
    {
        OutEmptyText.Visibility = _outRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InEmptyText.Visibility  = _inRules.Count  == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message  = message;
        StatusInfoBar.Title    = string.Empty;
        StatusInfoBar.IsOpen   = true;
    }

    // ---------- Keyboard reorder ----------

    /// <summary>
    /// Ctrl+Up / Ctrl+Down moves the focused (or selected) rule one place,
    /// giving keyboard and screen-reader users the same control as dragging.
    /// </summary>
    private void RulesListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || (e.Key != Key.Up && e.Key != Key.Down)) return;
        if (sender is not ListBox listBox || listBox.ItemsSource is not ObservableCollection<Rule> list) return;

        var rule = (Keyboard.FocusedElement as DependencyObject).FindAncestor<ListBoxItem>()?.DataContext as Rule
                   ?? listBox.SelectedItem as Rule;
        if (rule == null) return;

        var from = list.IndexOf(rule);
        var to = e.Key == Key.Up ? from - 1 : from + 1;
        e.Handled = true;
        if (from < 0 || to < 0 || to >= list.Count) return;

        CaptureContainerPositions(listBox);
        list.Move(from, to);
        ScheduleSlideAnimation(listBox);

        listBox.SelectedItem = rule;
        listBox.ScrollIntoView(rule);
        // The container is re-laid out by the move; focus it once layout settles.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            (listBox.ItemContainerGenerator.ContainerFromItem(rule) as ListBoxItem)?.Focus());
    }

    // ---------- Drag-and-drop reorder ----------

    private Point? _dragOrigin;
    private Rule? _draggedRule;

    private void RulesListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        // Skip drag setup if the click started on an interactive control. ButtonBase
        // covers both the buttons and the enable/disable ToggleButton (which
        // derives from ButtonBase), so one check is enough.
        if (src.FindAncestor<ButtonBase>() != null)
        {
            _dragOrigin = null;
            _draggedRule = null;
            return;
        }

        var listBox = (ListBox)sender;
        _dragOrigin = e.GetPosition(listBox);
        _draggedRule = src.FindAncestor<ListBoxItem>()?.DataContext as Rule;
    }

    private void RulesListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrigin == null || _draggedRule == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var listBox = (ListBox)sender;
        var pos = e.GetPosition(listBox);
        if (Math.Abs(pos.X - _dragOrigin.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragOrigin.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Dim the row being dragged so it's clear which one is in flight. The
        // container moves along with its item as we Move() during DragOver,
        // so the dimming follows the dragged row into its live position.
        var ghost = listBox.ItemContainerGenerator.ContainerFromItem(_draggedRule) as ListBoxItem;
        try
        {
            if (ghost != null) ghost.Opacity = 0.35;
            _isDragging = true;
            DragDrop.DoDragDrop(listBox, _draggedRule, DragDropEffects.Move);
        }
        finally
        {
            _isDragging = false;
            if (ghost != null) ghost.Opacity = 1.0;
            _dragOrigin = null;
            _draggedRule = null;
            // Drag is over: commit the new ordering exactly once.
            ScheduleSave();
        }
    }

    private void RulesListBox_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Rule)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        // Live reorder: move the dragged rule in the collection on every tick so
        // the ListBox reflows under the cursor. The Drop handler then has nothing
        // left to do.
        var dropped = (Rule)e.Data.GetData(typeof(Rule))!;
        var listBox = (ListBox)sender;
        if (listBox.ItemsSource is not ObservableCollection<Rule> collection) return;

        int sourceIdx = collection.IndexOf(dropped);
        if (sourceIdx < 0) return; // drag from the other column: don't touch this list

        int insertionPoint = ComputeInsertionPoint(listBox, e.GetPosition(listBox));
        int targetIdx = sourceIdx < insertionPoint ? insertionPoint - 1 : insertionPoint;
        if (targetIdx == sourceIdx) return;

        CaptureContainerPositions(listBox);
        collection.Move(sourceIdx, targetIdx);
        ScheduleSlideAnimation(listBox);
    }

    private void CaptureContainerPositions(ListBox listBox)
    {
        _capturedY.Clear();
        for (int i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
            if (!item.IsVisible) continue;
            // Capture the current VISUAL Y (layout + any in-flight TranslateTransform).
            // If we captured only the layout Y we'd snap mid-animation when a second
            // Move arrives, because the new animation would start from the new layout
            // delta and ignore the partially-completed previous slide.
            var layoutY = item.TransformToAncestor(listBox).Transform(new Point(0, 0)).Y;
            var transformY = (item.RenderTransform as TranslateTransform)?.Y ?? 0;
            _capturedY[item] = layoutY + transformY;
        }
    }

    private void ScheduleSlideAnimation(ListBox listBox)
    {
        // Dispatcher.BeginInvoke at Loaded priority runs the callback AFTER the
        // next layout pass (Render priority), exactly when the Move's
        // re-arrangement is reflected in TransformToAncestor results.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            for (int i = 0; i < listBox.Items.Count; i++)
            {
                if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
                if (!_capturedY.TryGetValue(item, out var oldY)) continue;
                var newY = item.TransformToAncestor(listBox).Transform(new Point(0, 0)).Y;
                var delta = oldY - newY;
                if (Math.Abs(delta) < 0.5) continue;

                if (item.RenderTransform is not TranslateTransform tt)
                {
                    tt = new TranslateTransform();
                    item.RenderTransform = tt;
                }
                var anim = new DoubleAnimation
                {
                    From = delta,
                    To = 0,
                    Duration = SlideDuration,
                    EasingFunction = SlideEase,
                    FillBehavior = FillBehavior.Stop,
                };
                tt.BeginAnimation(TranslateTransform.YProperty, anim);
            }
        });
    }

    private void RulesListBox_Drop(object sender, DragEventArgs e)
    {
        // All movement already happened in DragOver; just consume the event.
        e.Handled = true;
    }

    /// <summary>
    /// Returns the insertion point [0..count] that corresponds to the cursor Y:
    /// 0 = before first row, count = after last row. Splits each row at its
    /// vertical midpoint so half-hovering above an item means "insert before it".
    /// </summary>
    private static int ComputeInsertionPoint(ListBox listBox, Point cursor)
    {
        for (int i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
            if (!item.IsVisible) continue;
            var topLeft = item.TransformToAncestor(listBox).Transform(new Point(0, 0));
            var midY = topLeft.Y + item.RenderSize.Height / 2;
            if (cursor.Y < midY) return i;
        }
        return listBox.Items.Count;
    }
}

internal static class VisualTreeHelpers
{
    /// <summary>Walk up the visual tree until we hit a parent of type T.</summary>
    public static T? FindAncestor<T>(this DependencyObject? from) where T : DependencyObject
    {
        while (from != null && from is not T)
            from = from is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(from)
                : LogicalTreeHelper.GetParent(from);
        return from as T;
    }
}
