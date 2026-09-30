using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InstaDesktop.Services;
using Microsoft.Web.WebView2.Wpf;

namespace InstaDesktop;

// A separate top-level window for an Instagram call pop-up. WebViewService owns
// its CoreWebView2 (same environment and profile as the opener, assigned as
// NewWindow so window.opener and window.close() keep working).
internal sealed class CallWindow : Window
{
    public WebView2 View { get; }
    // Diagnostics only: keep test call windows invisible and out of the taskbar.
    internal static bool DiagnosticHidden { get; set; }
    private WindowState _beforeFullscreen;
    private bool _fullscreen;

    public CallWindow(double width, double height, Window? near)
    {
        Title = "Instagram call";
        Width = Math.Clamp(width, 480, 1600);
        Height = Math.Clamp(height, 360, 1200);
        MinWidth = 480;
        MinHeight = 360;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 0, 0));
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Icons/app.ico")); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
        if (near is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = near.Left + Math.Max(0, (near.ActualWidth - Width) / 2);
            Top = near.Top + Math.Max(0, (near.ActualHeight - Height) / 2);
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (DiagnosticHidden) { Opacity = 0; ShowInTaskbar = false; ShowActivated = false; }
        View = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(16, 16, 18) };
        Content = View;
        SourceInitialized += (_, _) => WindowTheme.UseDarkTitleBar(this);
    }

    public void SetFullscreen(bool fullscreen)
    {
        if (fullscreen == _fullscreen) return;
        _fullscreen = fullscreen;
        if (fullscreen)
        {
            _beforeFullscreen = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _beforeFullscreen;
        }
    }
}
