using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InstaDesktop.Localization;
using InstaDesktop.Services;

namespace InstaDesktop.Diagnostics;

// Explicit --settings-snapshot <png> mode only: renders the Settings window in
// an isolated profile (no WebView is loaded) for visual review. Optional:
// --snapshot-language=zh-Hant and --snapshot-theme=light.
internal static class SettingsSnapshot
{
    public static async Task RunAsync(MainWindow window, SettingsService settings, string output)
    {
        int exitCode = 1;
        try
        {
            settings.Current.AllowMicrophone = true;
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--snapshot-language=zh-Hant")) Loc.Use(UiLanguage.TraditionalChinese);
            if (args.Contains("--snapshot-theme=light")) ThemeService.Use(AppTheme.Light);
            var dialog = new SettingsWindow(settings, window.Web) { Owner = window, Height = 1480, ShowActivated = false };
            dialog.Show();
            await Task.Delay(1500);
            if (dialog.Content is FrameworkElement root)
            {
                // The whole scrollable page in one image, however long it is
                // (the window itself is limited to the screen height).
                root.UpdateLayout();
                var page = FindScroll(root)?.Content as FrameworkElement ?? root;
                var dpi = VisualTreeHelper.GetDpi(root);
                var area = new Rect(0, 0, page.ActualWidth, page.ActualHeight);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle((Brush)root.FindResource("AppBackground"), null, area);
                    dc.DrawRectangle(new VisualBrush(page), null, area);
                }
                var bitmap = new RenderTargetBitmap((int)(area.Width * dpi.DpiScaleX), (int)(area.Height * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(visual);
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
