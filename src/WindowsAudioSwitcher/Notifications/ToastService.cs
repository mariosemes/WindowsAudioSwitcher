using System.Windows;
using System.Windows.Threading;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Notifications;

public enum ToastKind
{
    /// <summary>A device switch happened. Green dot.</summary>
    Success,
    /// <summary>Neutral information such as an available update. Blue dot.</summary>
    Info,
    /// <summary>Something failed. Red dot, longer default duration, message may wrap.</summary>
    Error,
}

/// <summary>
/// Spawns transient floating notifications at the bottom-right of the primary
/// monitor's working area. Multiple toasts stack vertically.
/// </summary>
public sealed class ToastService
{
    private readonly Dispatcher _dispatcher;
    private readonly List<ToastWindow> _active = new();
    private const double EdgePadding = 4;   // outside the Border's own Margin
    private const double Spacing = 4;

    public ToastService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan ErrorDuration { get; set; } = TimeSpan.FromSeconds(6);

    /// <param name="onClick">
    /// Runs when the toast is clicked, before it closes. Null means a click
    /// just dismisses it.
    /// </param>
    public void Show(string title, string message, ToastKind kind = ToastKind.Success,
                     TimeSpan? duration = null, Action? onClick = null)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Show(title, message, kind, duration, onClick));
            return;
        }

        try
        {
            var toast = new ToastWindow(title, message, kind,
                duration ?? (kind == ToastKind.Error ? ErrorDuration : DefaultDuration), onClick);
            toast.Loaded += (_, _) =>
            {
                _active.Add(toast);
                Reposition();
            };
            toast.Closed += (_, _) =>
            {
                _active.Remove(toast);
                Reposition();
            };
            toast.Show();
        }
        catch (Exception ex)
        {
            Logger.Warn($"ToastService.Show failed: {ex.Message}");
        }
    }

    private void Reposition()
    {
        var work = SystemParameters.WorkArea;
        double bottom = work.Bottom - EdgePadding;

        // Bottom-up: newest toast hugs the taskbar, older ones move up.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var t = _active[i];
            // SizeToContent="Height": ActualHeight is valid after Loaded.
            t.Left = work.Right - t.ActualWidth - EdgePadding;
            t.Top  = bottom - t.ActualHeight;
            bottom -= t.ActualHeight + Spacing;
        }
    }
}
