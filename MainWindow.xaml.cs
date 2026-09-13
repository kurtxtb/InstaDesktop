using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using InstaDesktop.Models;
using InstaDesktop.Services;
using Forms = System.Windows.Forms;

namespace InstaDesktop;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly bool _startInBackground;
    private readonly WebViewService _web;
    internal NotificationService Notifications { get; }
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private bool _exiting;
    private bool _savingClose;
    private bool _disposed;
    private bool _initialized;
    private bool _trayHintShown;
    private bool _lastMaximized;
    internal WebViewService Web => _web;
    public static readonly DependencyProperty IsSidebarCompactProperty = DependencyProperty.Register(
        nameof(IsSidebarCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsSidebarCompact { get => (bool)GetValue(IsSidebarCompactProperty); private set => SetValue(IsSidebarCompactProperty, value); }
    internal NavigationSection SelectedSection { get; private set; } = NavigationSection.Home;
    private bool _mediaFullscreen;
    private WindowState _beforeFullscreenState;
    private Rect _beforeFullscreenBounds;
    private WindowChrome? _windowedChrome;

    public MainWindow(SettingsService settings, bool startInBackground)
    {
        _settings = settings;
        _startInBackground = startInBackground;
        InitializeComponent();
        RestoreWindow();
        Notifications = new NotificationService(Dispatcher, () => _settings.Current.AppNotifications, thread =>
        {
            ShowFromTray();
            _web?.NavigateNotificationThread(thread);
        });
        _web = new WebViewService(BrowserHost, this, settings, Notifications);
        // Keep the original Instagram page and native Emoji rendering. The
        // optional customization pipeline stays disabled for the default UI.
        _settings.Current.UiCustomization = false;
        _settings.Current.CompactInstagramLayout = false;
        _settings.Changed += SettingsChanged;
        SettingsChanged();
        _web.RouteChanged += SetSelectedSection;
        _web.HistoryChanged += (back, forward) => { BackButton.IsEnabled = back; ForwardButton.IsEnabled = forward; };
        _web.FullscreenChanged += SetMediaFullscreen;
        _web.UserNotice += ShowNotice;
        SetSelectedSection(NavigationSection.Home);
        _web.StatusChanged += (message, recoverable) =>
        {
            StatusText.Text = message;
            StatusHeading.Text = recoverable ? "Unable to load Instagram" : "Your Instagram, at home.";
            if (BrowserHost.Visibility == Visibility.Visible) StatusPanel.Visibility = Visibility.Visible;
            RecoveryActions.Visibility = recoverable ? Visibility.Visible : Visibility.Collapsed;
            RuntimeButton.Visibility = message.Contains("Runtime is required", StringComparison.Ordinal)
                ? Visibility.Visible : Visibility.Collapsed;
        };
        _web.Ready += () => StatusPanel.Visibility = Visibility.Collapsed;
        _web.ShowRequested += ShowFromTray;
        _web.CloseRequested += Close;
        _web.InjectionFailed += () => _tray?.ShowBalloonTip(5000, "UI customization could not load",
            "Instagram is still available. Check your CSS / JavaScript files and app log.", Forms.ToolTipIcon.Warning);
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WindowMessages);
            ClampWindowToMonitor();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        CreateTray();
        if (((App)Application.Current).DiagnosticMode)
        {
            if (((App)Application.Current).NotificationTest)
                await Diagnostics.NotificationTestRunner.RunAsync(this, _settings, ((App)Application.Current).DiagnosticOutput!);
            else if (((App)Application.Current).WindowLayoutTest)
                await Diagnostics.WindowLayoutTestRunner.RunAsync(this, ((App)Application.Current).DiagnosticOutput!);
            else
                await Diagnostics.SmokeTestRunner.RunAsync(this, _settings, ((App)Application.Current).DiagnosticOutput!);
            return;
        }
        if (_startInBackground) { Opacity = 0; ShowInTaskbar = false; }
        await _web.InitializeAsync();
        if (_startInBackground) { HideToTray(showHint: false); Opacity = 1; ShowInTaskbar = true; }
        UpdateBackgroundState();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Instagram", null, (_, _) => ShowFromTray());
        menu.Items.Add("Refresh", null, async (_, _) => await RunActionAsync(_web.ReloadAsync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add("Reload customization", null, async (_, _) => await RunActionAsync(_web.ReloadCustomizationAsync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "InstaDesktop", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    public void ShowFromTray()
    {
        if (_disposed) return;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _lastMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        if (!_web.NeedsRecovery) _web.View?.Focus();
        UpdateBackgroundState();
    }

    internal void HideToTray(bool showHint = true)
    {
        Hide();
        UpdateBackgroundState();
        if (showHint && !_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloonTip(3000, "InstaDesktop is running in the tray",
                "Right-click the tray icon for Settings or Exit.", Forms.ToolTipIcon.Info);
        }
    }

    private void ShowSettings()
    {
        ShowFromTray();
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings, _web) { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (_savingClose) return;
        _savingClose = true;
        try
        {
            if (_settings.Current.CloseButton == CloseButtonBehavior.MinimizeToTray)
            {
                HideToTray();
                await SavePlacementAsync();
            }
            else await ExitAsync();
        }
        finally { _savingClose = false; }
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        await SavePlacementAsync();
        DisposeResources();
        Application.Current.Shutdown();
    }

    internal void PrepareForShutdown() => _exiting = true;

    internal async Task SavePlacementAsync()
    {
        if (_mediaFullscreen) return;
        var settings = _settings.Current.Copy();
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            settings.Left = bounds.Left; settings.Top = bounds.Top;
            settings.Width = bounds.Width; settings.Height = bounds.Height;
        }
        settings.Maximized = _lastMaximized;
        try { await _settings.SaveAsync(settings); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    private void RestoreWindow()
    {
        var s = _settings.Current;
        Width = s.Width; Height = s.Height;
        if (s.Left.HasValue && s.Top.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.Left.Value; Top = s.Top.Value;
        }
        _lastMaximized = s.Maximized;
    }

    private void ClampWindowToMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var work = Forms.Screen.FromHandle(handle).WorkingArea;
        var source = HwndSource.FromHwnd(handle);
        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new System.Windows.Point(work.Left, work.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(work.Right, work.Bottom));
        double areaWidth = bottomRight.X - topLeft.X;
        double areaHeight = bottomRight.Y - topLeft.Y;
        MinWidth = Math.Min(760, areaWidth);
        MinHeight = Math.Min(600, areaHeight);
        Width = Math.Clamp(Width, MinWidth, areaWidth);
        Height = Math.Clamp(Height, MinHeight, areaHeight);
        if (double.IsNaN(Left)) Left = topLeft.X + (areaWidth - Width) / 2;
        if (double.IsNaN(Top)) Top = topLeft.Y + (areaHeight - Height) / 2;
        Left = Math.Clamp(Left, topLeft.X, bottomRight.X - Width);
        Top = Math.Clamp(Top, topLeft.Y, bottomRight.Y - Height);
        if (_lastMaximized) WindowState = WindowState.Maximized;
    }

    private async void Window_StateChanged(object? sender, EventArgs e)
    {
        if (!_mediaFullscreen && WindowState != WindowState.Minimized) _lastMaximized = WindowState == WindowState.Maximized;
        UpdateWindowFrame();
        if (MaximizeIcon is not null) MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized ? "M3,0 H10 V7 M0,3 H7 V10 H0 Z" : "M0,0 H10 V10 H0 Z");
        if (MaximizeButton is not null) MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        UpdateBackgroundState();
        if (_initialized && !_disposed) await SavePlacementAsync();
    }

    private IntPtr WindowMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO uses physical monitor pixels.
        {
            var screen = Forms.Screen.FromHandle(hwnd);
            var bounds = _mediaFullscreen ? screen.Bounds : screen.WorkingArea;
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaxPosition = new NativePoint { X = bounds.Left - screen.Bounds.Left, Y = bounds.Top - screen.Bounds.Top };
            info.MaxSize = new NativePoint { X = bounds.Width, Y = bounds.Height };
            info.MaxTrackSize.X = Math.Max(info.MaxTrackSize.X, bounds.Width);
            info.MaxTrackSize.Y = Math.Max(info.MaxTrackSize.Y, bounds.Height);
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
            return IntPtr.Zero;
        }
        // HTMAXBUTTON lets Windows 11 offer Snap Layout on the custom caption.
        if (!_mediaFullscreen && message == 0x0084 && MaximizeButton.IsVisible)
        {
            long point = lParam.ToInt64();
            var screenPoint = new System.Windows.Point((short)(point & 0xffff), (short)((point >> 16) & 0xffff));
            var local = MaximizeButton.PointFromScreen(screenPoint);
            if (new Rect(0, 0, MaximizeButton.ActualWidth, MaximizeButton.ActualHeight).Contains(local))
            { handled = true; return new IntPtr(9); }
        }
        if (!_mediaFullscreen && (message == 0x00A1 || message == 0x00A2) && wParam.ToInt32() == 9)
        {
            handled = true;
            if (message == 0x00A2) ToggleMaximize();
            return IntPtr.Zero;
        }
        const int WmExitSizeMove = 0x0232;
        if (message == WmExitSizeMove && _initialized && !_disposed)
            Dispatcher.BeginInvoke(new Action(() => { _ = RunActionAsync(SavePlacementAsync); }));
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    private void UpdateWindowFrame()
    {
        if (WindowFrame is null) return;
        // WindowChrome already accounts for the non-client resize frame.
        // A second inset exposes the Window background and shrinks the WebView.
        WindowFrame.Margin = new Thickness(0);
        WindowFrame.BorderThickness = new Thickness(_mediaFullscreen || WindowState == WindowState.Maximized ? 0 : 1);
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateBackgroundState();
    private void UpdateBackgroundState() => _web?.SetBackground(!IsVisible || WindowState == WindowState.Minimized);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        Func<Task>? action = null;
        if ((key == Key.F5 && modifiers == ModifierKeys.None) || (key == Key.R && modifiers == ModifierKeys.Control))
            action = _web.ReloadAsync;
        else if (key == Key.Left && modifiers == ModifierKeys.Alt)
            action = () => { if (_web.Core?.CanGoBack == true) _web.Core.GoBack(); return Task.CompletedTask; };
        else if (key == Key.Right && modifiers == ModifierKeys.Alt)
            action = () => { if (_web.Core?.CanGoForward == true) _web.Core.GoForward(); return Task.CompletedTask; };
        else if (key == Key.OemComma && modifiers == ModifierKeys.Control)
            action = () => { ShowSettings(); return Task.CompletedTask; };
        else if (key == Key.F12 && _settings.Current.DeveloperTools)
            action = () => { _web.Core?.OpenDevToolsWindow(); return Task.CompletedTask; };
        else if (modifiers == ModifierKeys.Control && key >= Key.D1 && key <= Key.D4)
            action = () => NavigateSectionAsync((NavigationSection)(key - Key.D1));
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.R)
            action = _web.ReloadCustomizationAsync;
        else if (key == Key.Escape && _mediaFullscreen)
            action = async () => { if (_web.Core is { } core) await core.ExecuteScriptAsync("if(document.fullscreenElement) document.exitFullscreen();"); SetMediaFullscreen(false); };
        if (action is null) return;
        e.Handled = true;
        if (!e.IsRepeat)
        {
            var pending = action;
            Dispatcher.BeginInvoke(new Action(() => { _ = RunActionAsync(pending); }));
        }
    }

    private static async Task RunActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await RunActionAsync(_web.ReloadAsync);
    private void Runtime_Click(object sender, RoutedEventArgs e) => ShellService.OpenWeb(NavigationPolicy.RuntimeDownload);

    public void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= SettingsChanged;
        Notifications.Dispose();
        _web.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
    }

    private void SettingsChanged()
    {
        if (_disposed) return;
        // Original Instagram mode: the WebView owns the page navigation. Keep
        // the legacy Native sidebar collapsed so it cannot leave a black gutter.
        IsSidebarCompact = true;
        SidebarColumn.Width = new GridLength(0);
        Sidebar.Visibility = Visibility.Collapsed;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { if (SidebarColumn is not null) SettingsChanged(); }
    private async void CollapseSidebar_Click(object sender, RoutedEventArgs e)
    {
        var next = _settings.Current.Copy();
        next.SidebarMode = next.SidebarMode == SidebarMode.Expanded ? SidebarMode.Compact : SidebarMode.Expanded;
        await RunActionAsync(() => _settings.SaveAsync(next));
    }
    private async void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton button && Enum.TryParse<NavigationSection>(button.Tag as string, out var section))
            await RunActionAsync(() => NavigateSectionAsync(section));
    }
    internal Task NavigateSectionAsync(NavigationSection section)
    {
        NoticePanel.Visibility = Visibility.Collapsed;
        NativePageTitle.Text = InstagramRoutes.TitleFor(section);
        NativePageSubtitle.Text = section switch
        {
            NavigationSection.Home => "Your space, redesigned for Windows.",
            NavigationSection.Messages => "Private conversations in a focused desktop workspace.",
            NavigationSection.Reels => "Short videos, presented in a clean native layout.",
            NavigationSection.Explore => "Discover something new.",
            NavigationSection.Search => "Find people and ideas.",
            NavigationSection.Notifications => "Stay up to date with your activity.",
            NavigationSection.Profile => "Your profile and account settings.",
            NavigationSection.Create => "Create and share something new.",
            _ => "Instagram desktop workspace."
        };
        NativeEmptyTitle.Text = section == NavigationSection.Create ? "Create a post" : $"{InstagramRoutes.TitleFor(section)} is ready";
        NativeEmptyText.Text = "This is a native InstaDesktop view. Instagram's web page is not displayed here.";
        return Task.CompletedTask;
    }

    private void NativeCreate_Click(object sender, RoutedEventArgs e) => _ = NavigateSectionAsync(NavigationSection.Create);
    private void NativeSignIn_Click(object sender, RoutedEventArgs e) => _ = RunActionAsync(() => _web.NavigateSectionAsync(NavigationSection.Home));
    internal void SetSelectedSection(NavigationSection section)
    {
        SelectedSection = section;
        SectionTitle.Text = InstagramRoutes.TitleFor(section);
        foreach (var button in new[] { HomeNav, MessagesNav, ReelsNav, ExploreNav, SearchNav, NotificationsNav, ProfileNav, CreateNav })
            button.IsChecked = string.Equals(button.Tag as string, section.ToString(), StringComparison.Ordinal);
    }
    private void ShowNotice(string message)
    {
        // Notifications use the native tray surface; navigation notices stay silent.
        if (message.Contains('\n'))
            _tray?.ShowBalloonTip(5000, "Instagram", message.Replace('\n', ' '), Forms.ToolTipIcon.Info);
        NoticePanel.Visibility = Visibility.Collapsed;
    }
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => NoticePanel.Visibility = Visibility.Collapsed;
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Back_Click(object sender, RoutedEventArgs e) { if (_web.Core?.CanGoBack == true) _web.Core.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (_web.Core?.CanGoForward == true) _web.Core.GoForward(); }
    private async void WebNavigation_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(() => _web.RevealWebNavigationAsync());
        NoticePanel.Visibility = Visibility.Collapsed;
    }
    internal void SetMediaFullscreen(bool fullscreen)
    {
        if (_mediaFullscreen == fullscreen) return;
        if (fullscreen)
        {
            _beforeFullscreenState = WindowState;
            _beforeFullscreenBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        }
        _mediaFullscreen = fullscreen;
        TitleBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Sidebar.Visibility = Toolbar.Visibility = Visibility.Collapsed;
        TitleRow.Height = new GridLength(fullscreen ? 0 : 40);
        ToolbarRow.Height = new GridLength(0);
        if (fullscreen) NoticePanel.Visibility = Visibility.Collapsed;
        SettingsChanged();
        if (fullscreen)
        {
            _windowedChrome = WindowChrome.GetWindowChrome(this);
            // Remove the work-area clipping used by WindowChrome for media.
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            WindowChrome.SetWindowChrome(this, null);
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowChrome.SetWindowChrome(this, _windowedChrome);
            ResizeMode = ResizeMode.CanResize;
            if (!_beforeFullscreenBounds.IsEmpty)
            { Left = _beforeFullscreenBounds.Left; Top = _beforeFullscreenBounds.Top; Width = _beforeFullscreenBounds.Width; Height = _beforeFullscreenBounds.Height; }
            WindowState = _beforeFullscreenState == WindowState.Minimized ? WindowState.Normal : _beforeFullscreenState;
        }
        UpdateWindowFrame();
    }
}
