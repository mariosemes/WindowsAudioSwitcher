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
using WindowsAudioSwitcher.Audio;
using WindowsAudioSwitcher.Rules;
using WindowsAudioSwitcher.Updates;

namespace WindowsAudioSwitcher;

public partial class MainWindow : FluentWindow
{
    private readonly ObservableCollection<Rule> _outRules = new();
    private readonly ObservableCollection<Rule> _inRules = new();
    private DispatcherTimer? _saveDebounce;
    private bool _loading;
    // True while a drag-and-drop is in flight. Suppresses the auto-save+apply
    // path so the ~800 ms COM-bound ApplyRules call can't freeze the UI mid-drag
    // when the 500 ms debounce ticks during a cursor pause.
    private bool _isDragging;

    // Captured Y of each visible ListBoxItem just BEFORE we mutate the rules
    // collection during a drag. After the next layout pass we use these to set
    // each row's TranslateTransform.Y to (old - new) and animate it back to 0,
    // which produces the slide-into-place feel during live reorder.
    private readonly Dictionary<ListBoxItem, double> _capturedY = new();
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly IEasingFunction SlideEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    public MainWindow()
    {
        InitializeComponent();
        // Stamp the running app's version into the title bar. Pulled at runtime
        // so dev builds, release builds, and pre-release builds each show what
        // they actually are. The visible chrome shows the version as a dimmed
        // header next to the title; the OS-level window title (taskbar / alt-
        // tab) gets the combined string since it can't carry styling.
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

        Loaded += (_, _) => RefreshCurrentDevices();
        TheApp.RulesApplied += OnRulesApplied;
        TheApp.UpdateAvailable += OnUpdateAvailable;
        // If the update check finished BEFORE this window was opened, surface it now.
        if (TheApp.PendingUpdate != null) OnUpdateAvailable(TheApp.PendingUpdate);
        Closed += (_, _) =>
        {
            TheApp.RulesApplied -= OnRulesApplied;
            TheApp.UpdateAvailable -= OnUpdateAvailable;
        };
    }

    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _downloadCts;

    private void OnUpdateAvailable(UpdateInfo info)
    {
        _pendingUpdate = info;
        UpdateBannerTitle.Text   = $"Update available — v{info.LatestVersion}";
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
        var url = _pendingUpdate.InstallerUrl;
        if (string.IsNullOrEmpty(url))
        {
            // No installer attached to the release — fall back to opening the release page.
            OpenInBrowser(_pendingUpdate.ReleaseUrl);
            return;
        }

        SetUpdateBannerDownloadingState();
        _downloadCts = new CancellationTokenSource();
        var versionLabel = $"v{_pendingUpdate.LatestVersion}";
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Percent is double pct)
            {
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = pct;
                var receivedMb = p.BytesReceived / 1024d / 1024d;
                var totalMb    = (p.TotalBytes ?? 0) / 1024d / 1024d;
                UpdateBannerMessage.Text = $"Downloading {versionLabel} — {pct:F0}%  ({receivedMb:F1} / {totalMb:F1} MB)";
            }
            else
            {
                UpdateProgressBar.IsIndeterminate = true;
                var receivedMb = p.BytesReceived / 1024d / 1024d;
                UpdateBannerMessage.Text = $"Downloading {versionLabel} — {receivedMb:F1} MB";
            }
        });

        try
        {
            var path = await UpdateInstaller.DownloadAsync(url, _pendingUpdate.ExpectedSha256, progress, _downloadCts.Token);
            UpdateBannerMessage.Text = $"Launching installer for {versionLabel}…";
            UpdateProgressBar.IsIndeterminate = true;
            // Hand off to Inno Setup. CloseApplications=force will terminate this
            // process if Shutdown doesn't beat the installer's first file write.
            UpdateInstaller.LaunchInstallerAndExit(path);
        }
        catch (OperationCanceledException)
        {
            UpdateBannerMessage.Text = $"Download cancelled. Click Update to try again.";
            SetUpdateBannerIdleState();
        }
        catch (Exception ex)
        {
            UpdateBannerMessage.Text = $"Update failed: {ex.Message}. Opening release page.";
            SetUpdateBannerIdleState();
            OpenInBrowser(_pendingUpdate.ReleaseUrl);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void UpdateCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private void UpdateDismissBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private static void OpenInBrowser(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* user can copy from log if it ever matters */ }
    }

    private App TheApp => (App)Application.Current;

    /// <summary>Intercept the X / Alt+F4 close so the app keeps running in the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowBounds();
        // Closing the window only hides it — the app lives on in the tray. The
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
            RunOnStartupCheck.IsChecked  = TheApp.Settings.RunOnStartup;
            StartMinimizedCheck.IsChecked = TheApp.Settings.StartMinimizedToTray;
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
            var screenArea  = SystemParameters.VirtualScreenLeft;
            var screenRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            var screenBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (left + 100 >= screenArea && left <= screenRight - 100 &&
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

    private void RefreshCurrentDevices()
    {
        var dm = TheApp.DeviceManager;
        var outSnap = dm.Snapshot(DataFlow.Render);
        var inSnap  = dm.Snapshot(DataFlow.Capture);
        var outName = outSnap.Devices.FirstOrDefault(d => string.Equals(d.Id, outSnap.CurrentDefaultId, StringComparison.OrdinalIgnoreCase))?.FriendlyName ?? "(none)";
        var inName  = inSnap.Devices.FirstOrDefault(d => string.Equals(d.Id, inSnap.CurrentDefaultId,  StringComparison.OrdinalIgnoreCase))?.FriendlyName  ?? "(none)";
        OutCurrentText.Text = outName;
        InCurrentText.Text  = inName;
    }

    private void OnRulesApplied(App.ApplyResult result)
    {
        OutCurrentText.Text = result.CurrentOutput?.FriendlyName ?? "(none)";
        InCurrentText.Text  = result.CurrentInput?.FriendlyName  ?? "(none)";

        if (result.ErrorMessage != null)
        {
            ShowStatus($"Apply failed: {result.ErrorMessage}", InfoBarSeverity.Error);
            return;
        }
        if (result.InitialRun) return;

        var parts = new List<string>();
        if (result.Output != null) parts.Add($"Output → {result.Output.FriendlyName}");
        if (result.Input  != null) parts.Add($"Input → {result.Input.FriendlyName}");
        if (parts.Count == 0)
            ShowStatus("Applied — no change needed.", InfoBarSeverity.Informational);
        else
            ShowStatus($"Applied: {string.Join("  ·  ", parts)}", InfoBarSeverity.Success);
    }

    // ---- Add-rule: single combined modal handles both exact-device and name-match. ----
    private void OutAddBtn_Click(object sender, RoutedEventArgs e) =>
        OpenAddRuleDialog(_outRules, DataFlow.Render, "Add output rule");

    private void InAddBtn_Click(object sender, RoutedEventArgs e) =>
        OpenAddRuleDialog(_inRules, DataFlow.Capture, "Add input rule");

    private void OpenAddRuleDialog(ObservableCollection<Rule> target, DataFlow flow, string title)
    {
        var devices = TheApp.DeviceManager.Snapshot(flow).Devices;
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

    private void RuleEditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Rule rule) return;

        ObservableCollection<Rule> list;
        DataFlow flow;
        string title;
        if (_outRules.Contains(rule))      { list = _outRules; flow = DataFlow.Render;  title = "Edit output rule"; }
        else if (_inRules.Contains(rule))  { list = _inRules;  flow = DataFlow.Capture; title = "Edit input rule"; }
        else return;

        var devices = TheApp.DeviceManager.Snapshot(flow).Devices;
        var dlg = new AddRuleDialog(title, devices, rule) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } newRule) return;

        var index = list.IndexOf(rule);
        if (index < 0) return;

        // Preserve on/off state across the swap — the user's editing the
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

    private void ApplyNowBtn_Click(object sender, RoutedEventArgs e) => TheApp.ApplyRules(initialRun: false);

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

    private void SettingsToggle_Changed(object sender, RoutedEventArgs e) => ScheduleSave();

    // ---- Auto-save plumbing -------------------------------------------------
    //
    // We persist+apply on a 500 ms debounce after any mutation, so the user no
    // longer needs an explicit "Save & Apply" button. Manual "Apply now" stays
    // for re-running the engine without any rule edit.

    private void OnRulesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyStates();
        // During a drag, Move() fires repeatedly. Skip the save+apply path —
        // we'll commit once in the drag's finally block.
        if (_isDragging) return;
        ScheduleSave();
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Rule.IsEnabled)) ScheduleSave();
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
        OutputPriority       = _outRules.ToList(),
        InputPriority        = _inRules.ToList(),
        RunOnStartup         = RunOnStartupCheck.IsChecked == true,
        StartMinimizedToTray = StartMinimizedCheck.IsChecked == true,
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

    // ---------- Drag-and-drop reorder ----------

    private Point? _dragOrigin;
    private Rule? _draggedRule;

    private void RulesListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        // Skip drag setup if the click started on an interactive control. ButtonBase
        // covers both the ✕ remove button and the enable/disable ToggleButton (which
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
            // Drag is over — commit the new ordering exactly once.
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
        if (sourceIdx < 0) return; // drag from the other column — don't touch this list

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
        // next layout pass (Render priority) — exactly when the Move's
        // re-arrangement is reflected in TransformToAncestor results.
        // LayoutUpdated would also fire here, but it fires for unrelated reasons
        // (e.g. RenderTransform invalidations) and we'd unsubscribe too early.
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
    /// Returns the insertion point [0..count] that corresponds to the cursor Y —
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
            from = VisualTreeHelper.GetParent(from);
        return from as T;
    }
}
