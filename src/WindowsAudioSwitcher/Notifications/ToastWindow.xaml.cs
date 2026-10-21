using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Notifications;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;
    private readonly Action? _onClick;
    private bool _closing;

    public ToastWindow(string title, string message, ToastKind kind, TimeSpan duration, Action? onClick)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        _onClick = onClick;

        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, kind switch
        {
            ToastKind.Error => "StatusErrorBrush",
            ToastKind.Info  => "AccentBlueBrush",
            _               => "StatusGoodBrush",
        });
        if (kind == ToastKind.Error)
        {
            // Error text is a sentence, not a device name; let it wrap to a few lines.
            MessageText.TextWrapping = TextWrapping.Wrap;
            MessageText.FontSize = 13;
            MessageText.MaxHeight = 64;
        }
        if (onClick != null) Cursor = System.Windows.Input.Cursors.Hand;

        _closeTimer = new DispatcherTimer { Interval = duration };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); BeginClose(); };

        Loaded += OnLoaded;
        MouseLeftButtonUp += (_, _) =>
        {
            if (_closing) return;
            try { _onClick?.Invoke(); }
            catch (Exception ex) { Logger.Warn($"Toast click action failed: {ex.Message}"); }
            BeginClose();
        };
        // Keep a toast on screen while the pointer is over it, so it can be read
        // or clicked; restart the countdown when the pointer leaves.
        MouseEnter += (_, _) => { if (!_closing) _closeTimer.Stop(); };
        MouseLeave += (_, _) => { if (!_closing) { _closeTimer.Stop(); _closeTimer.Start(); } };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Set WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW so we never steal focus
        // and don't show up in Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        _closeTimer.Start();
    }

    public void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _closeTimer.Stop();

        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
