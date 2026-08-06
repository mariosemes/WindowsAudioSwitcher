using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Rules;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // TolerantRuleKindConverter (specific to RuleKind) must come first so it
        // wins over the general enum converter, which handles any other enum by name.
        Converters =
        {
            new TolerantRuleKindConverter(),
            new JsonStringEnumConverter(),
        },
    };

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsAudioSwitcher");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static string BackupPath => SettingsPath + ".bak";

    public static AppSettings Load()
    {
        if (TryLoadFrom(SettingsPath, out var settings)) return settings;

        // The live file was missing, empty, or corrupt. Before falling back to
        // empty defaults — which would silently discard every rule the user set up —
        // try the backup that the last successful atomic Save left behind.
        if (File.Exists(BackupPath) && TryLoadFrom(BackupPath, out var fromBackup))
        {
            Logger.Warn($"settings.json was unreadable; recovered from backup ({BackupPath}).");
            try { File.Copy(BackupPath, SettingsPath, overwrite: true); } catch { /* best effort */ }
            return fromBackup;
        }

        // No usable backup either. Only a pre-existing-but-unparseable file is a
        // real problem worth shouting about; a fresh install simply has no file yet.
        if (File.Exists(SettingsPath))
        {
            Logger.Error($"settings.json exists but could not be parsed and no backup was usable; " +
                         $"starting from defaults. The unreadable file is preserved at {SettingsPath}.corrupt.");
            try { File.Copy(SettingsPath, SettingsPath + ".corrupt", overwrite: true); } catch { /* best effort */ }
        }
        return new AppSettings();
    }

    private static bool TryLoadFrom(string path, out AppSettings settings)
    {
        settings = new AppSettings();
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (parsed == null) return false;
            settings = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        // Atomic write: serialize to a sibling temp file, then swap it into place.
        // A crash or power-loss mid-write can only ever damage the temp file, never
        // the live settings.json — so the user can't lose their rules to a
        // half-written file. File.Replace also keeps the prior good copy as a .bak,
        // which Load() falls back to if the live file is ever damaged out of band.
        //
        // The temp name is unique per write so two overlapping Save() calls can't
        // write the same temp file or race on File.Replace (which would throw and,
        // because callers swallow it, silently drop a save). Replace/Move consumes
        // the temp on success; a failure leaves it behind, so we clean it up.
        var tmp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(SettingsPath))
            {
                File.Replace(tmp, SettingsPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, SettingsPath);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Deserializes <see cref="RuleKind"/> tolerantly: an unrecognized name (or an
/// out-of-range number, or null) degrades to <see cref="RuleKind.ExactDevice"/>
/// instead of throwing. Without this, a single unknown enum value — e.g. a rule
/// kind added by a newer version and then read by an older one — would fail the
/// whole settings file, and since the backup carries the same names Load() would
/// fall back to empty defaults and silently discard every rule the user set up.
/// Writes the enum name (same on-disk format as the default converter).
/// </summary>
internal sealed class TolerantRuleKindConverter : JsonConverter<RuleKind>
{
    public override RuleKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (Enum.TryParse<RuleKind>(s, ignoreCase: true, out var kind))
                return kind;
            Logger.Warn($"Unknown RuleKind \"{s}\" in settings.json — treating as {RuleKind.ExactDevice} (rule kept, not discarded).");
            return RuleKind.ExactDevice;
        }
        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt32(out var n) &&
            Enum.IsDefined(typeof(RuleKind), n))
        {
            return (RuleKind)n;
        }
        // Null, unexpected token, or out-of-range number — fall back safely.
        return RuleKind.ExactDevice;
    }

    public override void Write(Utf8JsonWriter writer, RuleKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
