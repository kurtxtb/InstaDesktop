using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InstaDesktop.Services;

namespace InstaDesktop.Diagnostics;

// Explicit --settings-snapshot <png> mode only: renders the Settings window in
// an isolated profile (no WebView is loaded) for visual review.
internal static class SettingsSnapshot
{
    public static async Task RunAsync(MainWindow window, SettingsService settings, string output)
    {
        int exitCode = 1;
        try
        {
            settings.Current.AllowMicrophone = true;
            var dialog = new SettingsWindow(settings, window.Web) { Owner = window, Height = 1480, ShowActivated = false };
            dialog.Show();
            await Task.Delay(1500);
            if (dialog.Content is FrameworkElement root)
            {
                // Expand the scroll area so the whole page lands in one image.
                if (LogicalTreeHelper.FindLogicalNode(root, "VersionText") is not null && FindScroll(root) is { } scroll)
                    scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                root.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(root);
                var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var file = File.Create(output);
                encoder.Save(file);
                exitCode = 0;
            }
            dialog.Close();
        }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
        finally
        {
            window.PrepareForShutdown(); window.DisposeResources();
            Application.Current.Shutdown(exitCode);
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject element && FindScroll(element) is { } found) return found;
        return null;
    }
}
