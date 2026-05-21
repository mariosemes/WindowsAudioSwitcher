namespace WindowsAudioSwitcher.Rules;

public sealed class AppSettings
{
    public List<Rule> OutputPriority { get; set; } = new();
    public List<Rule> InputPriority { get; set; } = new();
    public bool RunOnStartup { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = true;

    // Settings-window placement. Null = use WindowStartupLocation default.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}
