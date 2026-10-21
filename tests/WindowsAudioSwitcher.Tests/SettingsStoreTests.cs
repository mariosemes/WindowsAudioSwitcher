using System.IO;
using WindowsAudioSwitcher.Appearance;
using WindowsAudioSwitcher.Rules;
using Xunit;

namespace WindowsAudioSwitcher.Tests;

public class SettingsStoreTests
{
    private static AppSettings Sample() => new()
    {
        OutputPriority =
        {
            new Rule { Kind = RuleKind.ExactDevice, Value = "{dev-1}", Label = "Speakers", IsEnabled = false },
            new Rule { Kind = RuleKind.NameContains, Value = "Sony" },
        },
        InputPriority = { new Rule { Kind = RuleKind.NameContains, Value = "Yeti" } },
        RunOnStartup = false,
        StartMinimizedToTray = true,
        SwitchCommunicationsDevice = false,
        Theme = AppTheme.System,
        WindowLeft = 10, WindowTop = 20, WindowWidth = 900, WindowHeight = 650,
    };

    [Fact]
    public void SaveThenLoad_RoundTripsEverySetting()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");

        SettingsStore.Save(Sample(), path);
        var loaded = SettingsStore.Load(path, out var usedDefaults);

        Assert.False(usedDefaults);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(2, loaded.OutputPriority.Count);
        Assert.Equal((RuleKind.ExactDevice, "{dev-1}", "Speakers", false),
            (loaded.OutputPriority[0].Kind, loaded.OutputPriority[0].Value, loaded.OutputPriority[0].Label, loaded.OutputPriority[0].IsEnabled));
        Assert.Equal(RuleKind.NameContains, loaded.OutputPriority[1].Kind);
        Assert.Equal("Yeti", loaded.InputPriority.Single().Value);
        Assert.False(loaded.RunOnStartup);
        Assert.True(loaded.StartMinimizedToTray);
        Assert.False(loaded.SwitchCommunicationsDevice);
        Assert.Equal(AppTheme.System, loaded.Theme);
        Assert.Equal(900, loaded.WindowWidth);
    }

    [Fact]
    public void Save_WritesEnumsByName_AndNoUiOnlyFields()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var settings = Sample();
        settings.OutputPriority[1].SetStatus(RuleStatus.Active, "Sony");

        SettingsStore.Save(settings, path);
        var json = File.ReadAllText(path);

        Assert.Contains("\"NameContains\"", json);
        Assert.Contains("\"System\"", json);
        Assert.DoesNotContain("Status", json);
        Assert.DoesNotContain("DisplayText", json);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsAndReportsIt()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");

        Assert.False(SettingsStore.HasSavedSettingsAt(path));
        var loaded = SettingsStore.Load(path, out var usedDefaults);

        Assert.True(usedDefaults);
        Assert.Empty(loaded.OutputPriority);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.SwitchCommunicationsDevice);
    }

    [Fact]
    public void Load_CorruptFile_RecoversFromBackupAndRestoresLiveFile()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");

        SettingsStore.Save(Sample(), path);                 // creates live file
        SettingsStore.Save(Sample(), path);                 // File.Replace leaves a .bak
        Assert.True(File.Exists(path + ".bak"));
        File.WriteAllText(path, "{ this is not json");

        var loaded = SettingsStore.Load(path, out var usedDefaults);

        Assert.False(usedDefaults);
        Assert.Equal(2, loaded.OutputPriority.Count);
        Assert.Contains("Sony", File.ReadAllText(path));     // live file restored from .bak
    }

    [Fact]
    public void Load_CorruptFileWithoutBackup_PreservesItAsCorruptAndUsesDefaults()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.True(SettingsStore.HasSavedSettingsAt(path));
        var loaded = SettingsStore.Load(path, out var usedDefaults);

        Assert.True(usedDefaults);
        Assert.Empty(loaded.OutputPriority);
        Assert.Equal("{ this is not json", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void Load_UnknownEnumValues_FallBackInsteadOfDiscardingTheFile()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """
            {
              "OutputPriority": [
                { "Kind": "RegexMatch", "Value": "Sony.*", "IsEnabled": true },
                { "Kind": 42, "Value": "x" },
                { "Kind": "7", "Value": "y" },
                { "Kind": { "nested": true }, "Value": "z" },
                { "Kind": "namecontains", "Value": "Bose" }
              ],
              "Theme": "Neon"
            }
            """);

        var loaded = SettingsStore.Load(path, out var usedDefaults);

        Assert.False(usedDefaults);
        Assert.Equal(5, loaded.OutputPriority.Count);
        Assert.All(loaded.OutputPriority.Take(4), r => Assert.Equal(RuleKind.ExactDevice, r.Kind));
        Assert.Equal(RuleKind.NameContains, loaded.OutputPriority[4].Kind);   // case-insensitive name
        Assert.Equal("z", loaded.OutputPriority[3].Value);                    // reader stayed in sync
        Assert.Equal(AppTheme.Dark, loaded.Theme);
    }

    [Fact]
    public void Load_NullListsAndNullEntries_AreRepaired()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "OutputPriority": null, "InputPriority": [ null, { "Kind": "NameContains", "Value": "Yeti" } ] }""");

        var loaded = SettingsStore.Load(path, out _);

        Assert.NotNull(loaded.OutputPriority);
        Assert.Empty(loaded.OutputPriority);
        Assert.Equal("Yeti", loaded.InputPriority.Single().Value);
    }

    [Fact]
    public void Load_FileWithoutSchemaVersion_IsTreatedAsCurrentSchema()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "RunOnStartup": false }""");

        var loaded = SettingsStore.Load(path, out _);

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.False(loaded.RunOnStartup);
        Assert.True(loaded.SwitchCommunicationsDevice);   // new field defaults on for older files
    }

    [Fact]
    public void Save_LeavesNoTempFilesBehind()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        for (var i = 0; i < 3; i++) SettingsStore.Save(Sample(), path);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Tests_RunSandboxed_AwayFromRealAppData()
    {
        Assert.True(AppEnvironment.IsSandboxed);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            SettingsStore.SettingsPath, StringComparison.OrdinalIgnoreCase);
    }
}
