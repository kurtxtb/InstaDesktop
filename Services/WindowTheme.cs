using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InstaDesktop.Services;

// Native title bar in the current theme for standard-chrome windows (Settings,
// call and messages windows). Best effort: older Windows builds ignore it.
internal static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Applies now (or once the window has a handle) and whenever the theme changes.
    public static void Track(Window window)
    {
        void Apply() => ApplyTitleBar(window);
        window.SourceInitialized += (_, _) => Apply();
        ThemeService.Changed += Apply;
        window.Closed += (_, _) => ThemeService.Changed -= Apply;
        Apply();
    }

    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = ThemeService.IsDark ? 1 : 0;
        try
        {
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+); 19 on 1809-1909.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
