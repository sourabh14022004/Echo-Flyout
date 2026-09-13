using System;
using Microsoft.Win32;

namespace TaskbarMediaWidget.Windows;

/// <summary>
/// Watches the system light/dark app theme (the same registry value Settings > Personalization >
/// Colors writes) and raises <see cref="ThemeChanged"/> live, using the broadcast Windows already
/// sends on a theme change (WM_SETTINGCHANGE, surfaced via SystemEvents) rather than polling.
/// </summary>
public sealed class ThemeWatcher : IDisposable
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    public event EventHandler<bool>? ThemeChanged; // bool = isDarkMode

    private bool _isDarkMode;

    public bool IsDarkMode => _isDarkMode;

    public ThemeWatcher()
    {
        _isDarkMode = ReadIsDarkMode();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;

        bool isDark = ReadIsDarkMode();
        if (isDark == _isDarkMode) return;

        _isDarkMode = isDark;
        ThemeChanged?.Invoke(this, isDark);
    }

    private static bool ReadIsDarkMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            object? value = key?.GetValue(AppsUseLightThemeValue);
            // AppsUseLightTheme: 1 = light, 0 = dark. Default to dark if the value is missing.
            if (value is int lightThemeEnabled) return lightThemeEnabled == 0;
            return true;
        }
        catch
        {
            return true;
        }
    }
}
