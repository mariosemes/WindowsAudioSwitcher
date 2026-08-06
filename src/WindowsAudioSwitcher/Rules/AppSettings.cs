namespace WindowsAudioSwitcher.Rules;

public sealed class AppSettings
{
    public List<Rule> OutputPriority { get; set; } = new();
    public List<Rule> InputPriority { get; set; } = new();
    public bool RunOnStartup { get; set; } = true;

    // When true, a manual launch goes straight to the tray instead of opening the
    // settings window. Default false so a first launch (e.g. right after install)
    // is discoverable — it shows the window. Sign-in autostart is always silent to
    // the tray regardless (the Run-key command passes --tray).
    public bool StartMinimizedToTray { get; set; } = false;

    // Settings-window placement. Null = use WindowStartupLocation default.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}
