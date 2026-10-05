using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace InstaDesktop.Services;

public enum AppTheme { System, Dark, Light }

// Light or dark for InstaDesktop's own windows and the Instagram page.
// XAML refers to these brushes with DynamicResource, so a change applies to
// open windows at once; "System" follows Windows' app mode as it changes.
public static class ThemeService
{
    private static AppTheme _mode = AppTheme.Dark;
    private static bool _watching, _applied;
    public static bool IsDark { get; private set; } = true;
    public static event Action? Changed;
    // Windows' own light/dark (apps or taskbar) changed, whatever this app uses.
    public static event Action? WindowsThemeChanged;

    private static readonly Dictionary<string, (uint Dark, uint Light)> Palette = new()
    {
        ["AppBackground"] = (0xFF000000, 0xFFFFFFFF),
        ["SidebarBackground"] = (0xFF121212, 0xFFFAFAFA),
        ["CardBackground"] = (0xFF121214, 0xFFFAFAFA),
        ["Surface"] = (0xFF1C1C1E, 0xFFEFEFEF),
        ["HoverSurface"] = (0xFF262626, 0xFFE3E3E3),
        ["PressedSurface"] = (0xFF343438, 0xFFD4D4D4),
        ["SelectedSurface"] = (0xFF342B32, 0xFFE0F1FE),
        ["PrimaryText"] = (0xFFF5F5F5, 0xFF0A0A0A),
        ["SecondaryText"] = (0xFFA8A8A8, 0xFF656565),
        ["MutedText"] = (0xFF737373, 0xFF8E8E8E),
        ["Outline"] = (0xFF262626, 0xFFDBDBDB),
        ["Accent"] = (0xFF0095F6, 0xFF0095F6),
        ["ToggleTrack"] = (0xFF363636, 0xFFC7C7CC),
        ["ToggleThumb"] = (0xFFC9C9CF, 0xFFFFFFFF),
        ["FocusRing"] = (0xFFFFFFFF, 0xFF0A0A0A),
        ["ScrollThumb"] = (0xFF3A3A3C, 0xFFC7C7CC),
        ["ScrollThumbHover"] = (0xFF5A5A5E, 0xFFA0A0A5),
    };

    // The page background WebView2 paints before Instagram draws.
    public static System.Drawing.Color WebBackground => IsDark
        ? System.Drawing.Color.FromArgb(16, 16, 18) : System.Drawing.Color.White;

    public static void Use(AppTheme mode)
    {
        _mode = Enum.IsDefined(mode) ? mode : AppTheme.System;
        if (!_watching)
        {
            SystemEvents.UserPreferenceChanged += PreferenceChanged;
            _watching = true;
        }
        Refresh();
    }

    public static void Stop()
    {
        if (!_watching) return;
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        _watching = false;
    }

    private static void PreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_mode == AppTheme.System) Refresh();
            WindowsThemeChanged?.Invoke();
        }));
    }

    private static void Refresh()
    {
        bool dark = _mode switch { AppTheme.Dark => true, AppTheme.Light => false, _ => !WindowsAppsUseLightTheme() };
        bool changed = !_applied || dark != IsDark;
        IsDark = dark;
        _applied = true;
        if (!changed) return;
        if (Application.Current is { } app)
            foreach (var (key, colors) in Palette)
            {
                uint argb = dark ? colors.Dark : colors.Light;
                var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
                brush.Freeze();
                app.Resources[key] = brush;
            }
        Changed?.Invoke();
    }

    internal static bool WindowsAppsUseLightTheme() => ReadPersonalize("AppsUseLightTheme");

    // The taskbar has its own setting ("Choose your default Windows mode").
    public static bool TaskbarUsesLightTheme() => ReadPersonalize("SystemUsesLightTheme");

    private static bool ReadPersonalize(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int value && value == 1;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }
}
