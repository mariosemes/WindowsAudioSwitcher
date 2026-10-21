using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAudioSwitcher.Appearance;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Rules;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // The tolerant converters are specific to one enum each and must come
        // first so they win over the general converter, which handles any
        // other enum by name.
        Converters =
        {
            new TolerantEnumConverter<RuleKind>(RuleKind.ExactDevice),
            new TolerantEnumConverter<AppTheme>(AppTheme.Dark),
            new JsonStringEnumConverter(),
        },
    };

    public static string SettingsDirectory => AppEnvironment.DataDirectory;

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// False on the very first launch after a fresh install: neither the
    /// settings file nor its backup exists yet.
    /// </summary>
    public static bool HasSavedSettings => HasSavedSettingsAt(SettingsPath);

    internal static bool HasSavedSettingsAt(string path) =>
        File.Exists(path) || File.Exists(BackupPathFor(path));

    public static AppSettings Load() => Load(SettingsPath, out _);

    /// <param name="usedDefaults">True when nothing usable was on disk and defaults were returned.</param>
    public static AppSettings Load(out bool usedDefaults) => Load(SettingsPath, out usedDefaults);

    public static void Save(AppSettings settings) => Save(settings, SettingsPath);

    private static string BackupPathFor(string path) => path + ".bak";

    internal static AppSettings Load(string path, out bool usedDefaults)
    {
        usedDefaults = false;
        if (TryLoadFrom(path, out var settings)) return Migrate(settings);

        // The live file was missing, empty, or corrupt. Before falling back to
        // empty defaults (which would silently discard every rule the user set
        // up) try the backup that the last successful atomic Save left behind.
        var backup = BackupPathFor(path);
        if (File.Exists(backup) && TryLoadFrom(backup, out var fromBackup))
        {
            Logger.Warn($"settings.json was unreadable; recovered from backup ({backup}).");
            try { File.Copy(backup, path, overwrite: true); } catch { /* best effort */ }
            return Migrate(fromBackup);
        }

        // No usable backup either. Only a pre-existing-but-unparseable file is a
        // real problem worth shouting about; a fresh install simply has no file yet.
        if (File.Exists(path))
        {
            Logger.Error($"settings.json exists but could not be parsed and no backup was usable; " +
                         $"starting from defaults. The unreadable file is preserved at {path}.corrupt.");
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* best effort */ }
        }
        usedDefaults = true;
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
        catch (Exception ex)
        {
            Logger.Warn($"Could not read {path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Repairs hand-edited nulls and upgrades older schema versions in place.
    /// Add a step here whenever <see cref="AppSettings.CurrentSchemaVersion"/> is bumped.
    /// </summary>
    private static AppSettings Migrate(AppSettings s)
    {
        // "OutputPriority": null or [null] in a hand-edited file would otherwise
        // surface as a NullReferenceException far from here.
        s.OutputPriority ??= new List<Rule>();
        s.InputPriority  ??= new List<Rule>();
        s.OutputPriority.RemoveAll(r => r is null);
        s.InputPriority.RemoveAll(r => r is null);

        if (s.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            Logger.Warn($"settings.json has schema {s.SchemaVersion}, newer than this build understands " +
                        $"({AppSettings.CurrentSchemaVersion}). Unknown fields are ignored and will be dropped on the next save.");
        }

        // Future migrations go here, oldest first, e.g.
        //   if (s.SchemaVersion < 2) { ...; }

        s.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return s;
    }

    internal static void Save(AppSettings settings, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        // Atomic write: serialize to a sibling temp file, then swap it into place.
        // A crash or power-loss mid-write can only ever damage the temp file, never
        // the live settings.json. File.Replace also keeps the prior good copy as a
        // .bak, which Load() falls back to if the live file is ever damaged.
        //
        // The temp name is unique per write so two overlapping Save() calls can't
        // write the same temp file or race on File.Replace. Replace/Move consumes
        // the temp on success; a failure leaves it behind, so we clean it up.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
            {
                File.Replace(tmp, path, BackupPathFor(path), ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Deserializes an enum tolerantly: an unrecognized name, an undefined number,
/// null, or any other unexpected token degrades to a fallback value instead of
/// throwing. Without this, a single unknown value (e.g. a rule kind added by a
/// newer version and then read by an older one) would fail the whole settings
/// file, and since the backup carries the same names Load() would fall back to
/// empty defaults and silently discard every rule. Writes the enum name.
/// </summary>
internal sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private readonly T _fallback;

    public TolerantEnumConverter(T fallback) => _fallback = fallback;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                var s = reader.GetString();
                // Enum.TryParse also accepts numeric strings like "7", which may
                // not be defined, so check IsDefined too.
                if (Enum.TryParse<T>(s, ignoreCase: true, out var value) && Enum.IsDefined(value))
                    return value;
                Logger.Warn($"Unknown {typeof(T).Name} \"{s}\" in settings.json; using {_fallback} (setting kept, not discarded).");
                return _fallback;
            }
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(T), n))
                    return (T)Enum.ToObject(typeof(T), n);
                return _fallback;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                // Consume the whole value so the reader stays in sync.
                reader.Skip();
                return _fallback;
            default:
                return _fallback;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
