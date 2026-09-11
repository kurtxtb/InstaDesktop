using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace InstaDesktop.Diagnostics;

// Explicit, offline diagnostic mode; App isolates all settings from the user's profile.
internal static class WindowLayoutTestRunner
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    internal static async Task RunAsync(MainWindow window, string output)
    {
        var checks = new Dictionary<string, object>();
        int exitCode = 1;
        try
        {
            var host = (Grid)window.FindName("BrowserHost");
            var frame = (Border)window.FindName("WindowFrame");
            var toolbar = (RowDefinition)window.FindName("ToolbarRow");
            var sidebar = (ColumnDefinition)window.FindName("SidebarColumn");
            var handle = new WindowInteropHelper(window).Handle;

            void Check(bool condition, string name)
            {
                checks[name] = condition;
                if (!condition) throw new InvalidOperationException(name);
            }

            async Task SettleAsync()
            {
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                await Task.Delay(120); // Allow native sizing messages to finish.
                window.UpdateLayout();
            }

            void CheckSettingsHitTarget(string label)
            {
                var button = (Button)window.FindName("TitleSettingsButton");
                var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                int coordinates = (unchecked((ushort)(short)center.Y) << 16) | unchecked((ushort)(short)center.X);
                // Exercise WindowChrome's native hit test: HTCLIENT receives button
                // clicks; HTCAPTION would silently turn them into window dragging.
                var hit = SendMessage(handle, 0x0084, IntPtr.Zero, new IntPtr(coordinates));
                Check(button.IsVisible && button.IsEnabled && hit.ToInt64() == 1, label + " settings accepts pointer input");
            }

            void CheckEdges(System.Drawing.Rectangle area, string label, bool fullscreen)
            {
                var top = host.PointToScreen(new Point(0, 0));
                var bottom = host.PointToScreen(new Point(host.ActualWidth, host.ActualHeight));
                checks[label + " geometry"] = new { left = top.X, top = top.Y, right = bottom.X, bottom = bottom.Y,
                    area.Left, area.Top, area.Right, area.Bottom };
                Check(Math.Abs(top.X - area.Left) <= 1 && Math.Abs(bottom.X - area.Right) <= 1 &&
                    Math.Abs(bottom.Y - area.Bottom) <= 1, label + " content reaches work-area edges");
                Check(fullscreen ? Math.Abs(top.Y - area.Top) <= 1 : top.Y > area.Top,
                    label + " title height");
                Check(frame.Margin == new Thickness(0) && frame.BorderThickness == new Thickness(0), label + " no outer gutter");
                Check(toolbar.ActualHeight == 0 && sidebar.ActualWidth == 0, label + " no legacy toolbar or sidebar");
            }

            foreach (var screen in Forms.Screen.AllScreens)
            {
                window.WindowState = WindowState.Normal;
                var source = HwndSource.FromHwnd(handle);
                var position = source.CompositionTarget.TransformFromDevice.Transform(new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40));
                window.Left = position.X;
                window.Top = position.Y;
                window.Width = 900;
                window.Height = 650;
                await SettleAsync();
                var restored = new Rect(window.Left, window.Top, window.Width, window.Height);
                CheckSettingsHitTarget(screen.DeviceName + " normal");
                window.WindowState = WindowState.Maximized;
                await SettleAsync();
                CheckEdges(Forms.Screen.FromHandle(handle).WorkingArea, screen.DeviceName + " maximized", false);
                CheckSettingsHitTarget(screen.DeviceName + " maximized");

                window.SetMediaFullscreen(true);
                await SettleAsync();
                CheckEdges(Forms.Screen.FromHandle(handle).Bounds, screen.DeviceName + " fullscreen", true);
                window.SetMediaFullscreen(false);
                await SettleAsync();
                Check(window.WindowState == WindowState.Maximized, screen.DeviceName + " restores maximized state");
                CheckEdges(Forms.Screen.FromHandle(handle).WorkingArea, screen.DeviceName + " returned", false);
                CheckSettingsHitTarget(screen.DeviceName + " returned from fullscreen");

                window.WindowState = WindowState.Normal;
                await SettleAsync();
                Check(Math.Abs(window.Width - restored.Width) <= 1 && Math.Abs(window.Height - restored.Height) <= 1 &&
                    Math.Abs(window.Left - restored.Left) <= 1 && Math.Abs(window.Top - restored.Top) <= 1,
                    screen.DeviceName + " restores original bounds");
                window.SetMediaFullscreen(true);
                await SettleAsync();
                window.SetMediaFullscreen(false);
                await SettleAsync();
                Check(window.WindowState == WindowState.Normal && toolbar.ActualHeight == 0 && sidebar.ActualWidth == 0 &&
                    Math.Abs(window.Width - restored.Width) <= 1 && Math.Abs(window.Height - restored.Height) <= 1,
                    screen.DeviceName + " fullscreen restores normal layout");
            }
            checks["note"] = "Offline WPF host geometry on connected monitors; no signed-in content or media playback tested.";
            exitCode = 0;
        }
        catch (Exception error)
        {
            checks["failure"] = error.GetType().Name;
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = exitCode == 0, checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.PrepareForShutdown();
            window.DisposeResources();
            Application.Current.Shutdown(exitCode);
        }
    }
}
