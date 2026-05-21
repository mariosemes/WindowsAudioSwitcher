using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WindowsAudioSwitcher.Notifications;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;
    private bool _closing;

    public ToastWindow(string title, string message, TimeSpan duration)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        _closeTimer = new DispatcherTimer { Interval = duration };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); BeginClose(); };

        Loaded += OnLoaded;
        MouseLeftButtonUp += (_, _) => BeginClose();
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
