using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using InstaDesktop.Localization;
using InstaDesktop.Services;
using Microsoft.Web.WebView2.Wpf;

namespace InstaDesktop;

// A top-level window around a second Instagram WebView that WebViewService owns
// (same environment and profile as the main page): call pop-ups and the
// messages window. Native title bar in the app theme, "Always on top" in its
// title bar menu, and fullscreen for videos.
internal abstract class PopoutWindow : Window
{
    public WebView2 View { get; }
    // Diagnostics only: keep test windows invisible and out of the taskbar.
    internal static bool DiagnosticHidden { get; set; }
    private WindowState _beforeFullscreen;
    private bool _fullscreen;

    protected PopoutWindow(string title, bool onTop, Brush background, System.Drawing.Color webBackground)
    {
        Topmost = onTop;
        Title = title;
        ShowInTaskbar = true;
        Background = background;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Icons/app.ico")); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
        if (DiagnosticHidden) { Opacity = 0; ShowInTaskbar = false; ShowActivated = false; }
        View = new WebView2 { DefaultBackgroundColor = webBackground };
        Content = View;
        WindowTheme.Track(this);
        SourceInitialized += (_, _) => AddOnTopMenu();
    }

    // Centered over the main window when it is on screen.
    protected void PlaceNear(Window? near)
    {
        if (near is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = near.Left + Math.Max(0, (near.ActualWidth - Width) / 2);
            Top = near.Top + Math.Max(0, (near.ActualHeight - Height) / 2);
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    // "Always on top" in the title bar menu (right-click the title, or Alt+Space).
    private const int WmSysCommand = 0x0112;
    private const uint OnTopCommand = 0x1000, MfSeparator = 0x800, MfChecked = 0x8;

    private void AddOnTopMenu()
    {
        var handle = new WindowInteropHelper(this).Handle;
        IntPtr menu = GetSystemMenu(handle, false);
        if (menu == IntPtr.Zero) return;
        AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
        AppendMenu(menu, Topmost ? MfChecked : 0, (UIntPtr)OnTopCommand, Loc.T("Call.OnTop"));
        HwndSource.FromHwnd(handle)?.AddHook(SystemMenuHook);
    }

    private IntPtr SystemMenuHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == OnTopCommand)
        {
            SetOnTop(!Topmost);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // Close() may only hide some windows (the messages window); this always closes.
    protected bool ClosingForGood { get; private set; }
    public void CloseForGood()
    {
        ClosingForGood = true;
        Close();
    }

    public virtual void SetOnTop(bool onTop)
    {
        Topmost = onTop;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        IntPtr menu = GetSystemMenu(handle, false);
        if (menu != IntPtr.Zero) CheckMenuItem(menu, OnTopCommand, onTop ? MfChecked : 0);
    }

    // Diagnostics: whether the title bar menu shows the item checked.
    internal bool OnTopMenuChecked
    {
        get
        {
            IntPtr menu = GetSystemMenu(new WindowInteropHelper(this).Handle, false);
            return menu != IntPtr.Zero && (GetMenuState(menu, OnTopCommand, 0) & MfChecked) != 0;
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern uint CheckMenuItem(IntPtr menu, uint id, uint check);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint id, uint flags);

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

// An Instagram call pop-up, assigned as the opener's NewWindow so window.opener
// and window.close() keep working. Video calls stay black in either theme.
internal sealed class CallWindow : PopoutWindow
{
    public CallWindow(double width, double height, Window? near, bool onTop = false)
        : base(Loc.T("Web.CallTitle"), onTop, Brushes.Black, System.Drawing.Color.FromArgb(16, 16, 18))
    {
        Width = Math.Clamp(width, 480, 1600);
        Height = Math.Clamp(height, 360, 1200);
        MinWidth = 480;
        MinHeight = 360;
        PlaceNear(near);
    }
}

// A floating inbox panel, like the chat pop-outs of desktop messengers: no
// browser-style title bar, a slim header (title, unread count, pin, open in
// the main window, close), a shadow, rounded corners on Windows 11. Close only
// hides it so it comes back instantly where the conversation was.
internal sealed class MessagesWindow : PopoutWindow
{
    private const double HeaderHeight = 44, Inset = 5;
    private readonly Border _unread;
    private readonly TextBlock _unreadText;
    private readonly Button _pin;
    private readonly Path _pinIcon;
    public event Action? OpenInMainRequested;
    public event Action? Hidden;

    public MessagesWindow(double width, double height, double? left, double? top, Window? near, bool onTop = false)
        : base(Loc.T("Web.MessagesTitle"), onTop, Brushes.Transparent, ThemeService.WebBackground)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "SidebarBackground");
        // The DWM shadow needs a sliver of glass frame; the header is the caption.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = HeaderHeight, ResizeBorderThickness = new Thickness(Inset + 1),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
        MinWidth = 340;
        MinHeight = 420;
        Width = width;
        Height = height;

        // Header
        var title = new TextBlock
        {
            Text = Loc.T("Messages.Title"), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 1), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft JhengHei UI")
        };
        _unreadText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };
        _unread = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x30, 0x40)), CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18,
            Padding = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Child = _unreadText
        };
        var icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        try { icon.Source = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Icons/app.ico")); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(icon);
        titleRow.Children.Add(title);
        titleRow.Children.Add(_unread);

        _pinIcon = HeaderIcon("M9,3 H15 M10,3 V9 L7,13 H17 L14,9 V3 M12,13 V21");
        _pin = HeaderButton(_pinIcon, Loc.T("Messages.Pin"), (_, _) => SetOnTop(!Topmost));
        var openInMain = HeaderButton(HeaderIcon("M14,4 H20 V10 M20,4 L11,13 M18,14 V20 H4 V6 H10"), Loc.T("Messages.OpenInMain"),
            (_, _) => OpenInMainRequested?.Invoke());
        var hide = HeaderButton(HeaderIcon("M6,6 L18,18 M18,6 L6,18"), Loc.T("Messages.Close"), (_, _) => Close());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
        buttons.Children.Add(_pin);
        buttons.Children.Add(openInMain);
        buttons.Children.Add(hide);
        var header = new Grid { Height = HeaderHeight };
        header.Children.Add(titleRow);
        header.Children.Add(buttons);

        // The page sits inset in the panel, which also leaves room to resize:
        // the WebView's own window would otherwise swallow the edges.
        Content = null;
        var page = new Border { Margin = new Thickness(Inset, 0, Inset, Inset), Child = View };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(page, 1);
        layout.Children.Add(header);
        layout.Children.Add(page);
        var frame = new Border { BorderThickness = new Thickness(1), Child = layout };
        frame.SetResourceReference(Border.BorderBrushProperty, "Outline");
        frame.SetResourceReference(Border.BackgroundProperty, "SidebarBackground");
        Content = frame;

        Place(left, top, near);
        SourceInitialized += (_, _) => RoundCorners();
        ShowOnTopState();
    }

    private static Path HeaderIcon(string data)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data), Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        };
        path.SetResourceReference(Shape.StrokeProperty, "PrimaryText");
        return path;
    }

    private static Button HeaderButton(Path icon, string label, RoutedEventHandler click)
    {
        var button = new Button { Content = icon, ToolTip = label, Width = 34, Height = 32, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties_SetName(button, label);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += click;
        return button;
    }

    private static void AutomationProperties_SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    // First time: a flyout above the taskbar's right end, on the main window's
    // screen. Later: where the user left it, if that is still on a screen.
    private void Place(double? left, double? top, Window? near)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var all = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (left is double x && top is double y && all.IntersectsWith(new Rect(x, y, Width, HeaderHeight)))
        {
            Left = x;
            Top = y;
            return;
        }
        var work = WorkAreaOf(near);
        Height = Math.Min(Height, work.Height - 24);
        Left = work.Right - Width - 12;
        Top = work.Bottom - Height - 12;
    }

    private static Rect WorkAreaOf(Window? near)
    {
        try
        {
            var screen = near is not null && new WindowInteropHelper(near).Handle is var handle && handle != IntPtr.Zero
                ? System.Windows.Forms.Screen.FromHandle(handle) : System.Windows.Forms.Screen.PrimaryScreen!;
            var area = screen.WorkingArea;
            var source = near is not null ? PresentationSource.FromVisual(near) : null;
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = transform.Transform(new Point(area.Left, area.Top));
            var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));
            return new Rect(topLeft, bottomRight);
        }
        catch (Exception error) when (error is InvalidOperationException or NullReferenceException)
        {
            return SystemParameters.WorkArea;
        }
    }

    // Windows 11 rounds the whole window, web content included; Windows 10
    // keeps square corners (it cannot clip the WebView's own window).
    private void RoundCorners()
    {
        var handle = new WindowInteropHelper(this).Handle;
        int round = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public override void SetOnTop(bool onTop)
    {
        base.SetOnTop(onTop);
        ShowOnTopState();
    }

    private void ShowOnTopState()
    {
        if (_pinIcon is null) return;
        if (Topmost) _pinIcon.SetResourceReference(Shape.StrokeProperty, "Accent");
        else _pinIcon.SetResourceReference(Shape.StrokeProperty, "PrimaryText");
        _pin.ToolTip = Loc.T(Topmost ? "Messages.Unpin" : "Messages.Pin");
    }

    // Diagnostics: the header's "open in main window" button.
    internal void RequestOpenInMain() => OpenInMainRequested?.Invoke();

    public void SetUnread(int count)
    {
        _unreadText.Text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _unread.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal string UnreadShown => _unread.Visibility == Visibility.Visible ? _unreadText.Text : "";

    // Close hides; the app closing, a crash or the idle timeout close for good.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (ClosingForGood || e.Cancel) return;
        e.Cancel = true;
        Hide();
        Hidden?.Invoke();
    }
}
