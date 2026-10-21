using WindowsAudioSwitcher.Appearance;

namespace WindowsAudioSwitcher.Rules;

public sealed class AppSettings
{
    /// <summary>
    /// Bumped whenever the on-disk shape changes in a way that needs a
    /// migration. SettingsStore.Load runs migrations for older files.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    // A file without this field predates versioning and is schema 1.
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<Rule> OutputPriority { get; set; } = new();
    public List<Rule> InputPriority { get; set; } = new();

    // Only the default for a file that already exists but lacks the field.
    // On a fresh install App seeds this from the Run key the installer wrote,
    // so the installer's "Launch on sign-in" choice is respected.
    public bool RunOnStartup { get; set; } = true;

    // When true, a manual launch goes straight to the tray instead of opening the
    // settings window. Default false so a first launch (e.g. right after install)
    // is discoverable: it shows the window. Sign-in autostart is always silent to
    // the tray regardless (the Run-key command passes --tray).
    public bool StartMinimizedToTray { get; set; } = false;

    // When false, only the Console and Multimedia roles follow the rules, so a
    // headset kept as the Communications device (Teams, Discord, Zoom) stays put.
    public bool SwitchCommunicationsDevice { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // Settings-window placement. Null = use WindowStartupLocation default.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}
