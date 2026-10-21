using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WindowsAudioSwitcher.Logging;

namespace WindowsAudioSwitcher.Appearance;

public enum AppTheme
{
    Dark,
    Light,
    /// <summary>Follow the Windows "app mode" setting and switch live when it changes.</summary>
    System,
}

/// <summary>
/// Applies the app theme in two layers: WPF UI's own control resources, and
/// our palette dictionary (Palette.Dark.xaml / Palette.Light.xaml) holding the
/// brushes the XAML references via DynamicResource.
/// </summary>
public static class ThemeService
{
    private const string PaletteMarker = "/Appearance/Palette.";

    private static AppTheme _requested = AppTheme.Dark;
    private static bool _watchingSystem;

    public static ApplicationTheme Effective { get; private set; } = ApplicationTheme.Dark;

    /// <summary>Raised on the UI thread after the effective theme changes.</summary>
    public static event Action? Changed;

    /// <summary>Switches theme at runtime. No-op when the effective theme is unchanged.</summary>
    public static void Apply(AppTheme theme) => Apply(theme, force: false);

    /// <summary>Applies the startup theme unconditionally, before any window exists.</summary>
    public static void Initialize(AppTheme theme) => Apply(theme, force: true);

    private static void Apply(AppTheme theme, bool force)
    {
        _requested = theme;
        if (theme == AppTheme.System && !_watchingSystem)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _watchingSystem = true;
        }
        else if (theme != AppTheme.System && _watchingSystem)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _watchingSystem = false;
        }
        ApplyEffective(Resolve(theme), force);
    }

    private static ApplicationTheme Resolve(AppTheme theme) => theme switch
    {
        AppTheme.Light => ApplicationTheme.Light,
        AppTheme.System => SystemUsesLightTheme() ? ApplicationTheme.Light : ApplicationTheme.Dark,
        _ => ApplicationTheme.Dark,
    };

    internal static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyEffective(ApplicationTheme theme, bool force)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!force && theme == Effective) return;

        try
        {
            var dictionaries = app.Resources.MergedDictionaries;
            var name = theme == ApplicationTheme.Light ? "Light" : "Dark";
            var fresh = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,{PaletteMarker}{name}.xaml", UriKind.Absolute),
            };
            var existing = dictionaries.FirstOrDefault(d =>
                d.Source?.OriginalString.Contains(PaletteMarker, StringComparison.OrdinalIgnoreCase) == true);
            if (existing != null) dictionaries[dictionaries.IndexOf(existing)] = fresh;
            else dictionaries.Add(fresh);

            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);

            // ApplicationThemeManager only refreshes the main window's backdrop.
            foreach (Window w in app.Windows)
            {
                if (w is FluentWindow fw)
                    WindowBackgroundManager.UpdateBackground(fw, theme, fw.WindowBackdropType);
            }

            Effective = theme;
            Logger.Info($"Theme applied: {theme} (requested {_requested}).");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Applying theme {theme} failed: {ex.Message}");
        }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Windows reports an app-mode (light/dark) switch as a General change.
        if (e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_requested == AppTheme.System) ApplyEffective(Resolve(AppTheme.System), force: false);
        });
    }
}
