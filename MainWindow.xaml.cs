using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using InstaDesktop.Localization;
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
    private bool _mediaFullscreen;
    private WindowState _beforeFullscreenState;
    private Rect _beforeFullscreenBounds;
    private WindowChrome? _windowedChrome;

    private readonly AppCommand _startCommand;

    public MainWindow(SettingsService settings, bool startInBackground, AppCommand startCommand = AppCommand.None)
    {
        _settings = settings;
        _startInBackground = startInBackground;
        _startCommand = startCommand;
        InitializeComponent();
        RestoreWindow();
        Notifications = new NotificationService(Dispatcher, () => NotificationsActive, OpenFromNotification)
        {
            SoundEnabled = () => _settings.Current.NotificationSound,
            HideContent = () => _settings.Current.HideNotificationContent,
            IsMuted = thread => _settings.Current.IsMuted(thread, DateTimeOffset.UtcNow)
        };
        Notifications.Presented += FlashForNewMessage;
        Notifications.MuteRequested += thread => _ = MuteConversationAsync(thread);
        _web = new WebViewService(BrowserHost, this, settings, Notifications);
        _web.FullscreenChanged += SetMediaFullscreen;
        _web.UserNotice += ShowNotice;
        _web.MediaBlocked += ShowMediaBlocked;
        _web.ZoomChanged += ShowZoom;
        _web.UnreadCountChanged += _ => UpdateUnreadBadge();
        _web.MediaDownloadFinished += ShowMediaDownload;
        _settings.Changed += SettingsChanged;
        ThemeService.WindowsThemeChanged += WindowsThemeChanged;
        _web.StatusChanged += (message, recoverable) =>
        {
            StatusText.Text = message;
            StatusHeading.Text = Loc.T(recoverable ? "Main.HeadingError" : "Main.Heading");
            if (BrowserHost.Visibility == Visibility.Visible) StatusPanel.Visibility = Visibility.Visible;
            RecoveryActions.Visibility = recoverable ? Visibility.Visible : Visibility.Collapsed;
            RuntimeButton.Visibility = _web.RuntimeMissing ? Visibility.Visible : Visibility.Collapsed;
        };
        _web.Ready += () => StatusPanel.Visibility = Visibility.Collapsed;
        _web.ShowRequested += ShowFromTray;
        _web.CloseRequested += Close;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WindowMessages);
            ClampWindowToMonitor();
            // Diagnostics never take the user's real system-wide shortcuts.
            if (!((App)Application.Current).DiagnosticMode) ApplyHotkeys();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        CreateTray();
        if (((App)Application.Current).DiagnosticMode)
        {
            if (((App)Application.Current).InboxDiagnostics)
                await Diagnostics.InboxDiagnosticsRunner.RunAsync(this, _settings, ((App)Application.Current).DiagnosticOutput!,
                    ((App)Application.Current).InboxDiagnosticSeconds);
            else if (((App)Application.Current).NotificationTest)
                await Diagnostics.NotificationTestRunner.RunAsync(this, _settings, ((App)Application.Current).DiagnosticOutput!);
            else if (((App)Application.Current).SettingsSnapshot)
                await Diagnostics.SettingsSnapshot.RunAsync(this, _settings, ((App)Application.Current).DiagnosticOutput!);
            else if (((App)Application.Current).MediaPermissionTest)
                await Diagnostics.MediaPermissionTests.RunAsync(this, _settings, ((App)Application.Current).DiagnosticOutput!);
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
        if (_startCommand != AppCommand.None) RunCommand(_startCommand);
    }

    // A clicked notification: a conversation goes to the messages panel when
    // chosen in Settings (and the main page is ready to lend it its profile).
    private void OpenFromNotification(string? thread)
    {
        if (_disposed) return;
        if (thread is not null && _settings.Current.OpenNotificationsIn == NotificationOpenTarget.MessagesPanel && _web.CanOpenMessagesWindow)
        {
            _ = RunActionAsync(() => _web.OpenMessagesWindowAsync(thread));
            return;
        }
        ShowFromTray();
        _web.NavigateNotificationThread(thread);
    }

    // ---- System-wide shortcuts ------------------------------------------------

    internal const int HotkeyShowWindowId = 1, HotkeyMessagesId = 2;
    private GlobalHotkeys? _hotkeys;
    private string? _appliedHotkeys;
    // Shortcuts another app already owns (Settings shows them).
    internal HashSet<int> HotkeyConflicts { get; } = new();

    internal void ApplyHotkeys(bool force = false)
    {
        if (_disposed) return;
        string wanted = _settings.Current.HotkeyShowWindow + "|" + _settings.Current.HotkeyMessages;
        if (!force && wanted == _appliedHotkeys) return;
        _appliedHotkeys = wanted;
        _hotkeys ??= new GlobalHotkeys(new WindowInteropHelper(this).Handle);
        HotkeyConflicts.Clear();
        if (!_hotkeys.Register(HotkeyShowWindowId, Hotkey.Parse(_settings.Current.HotkeyShowWindow))) HotkeyConflicts.Add(HotkeyShowWindowId);
        if (!_hotkeys.Register(HotkeyMessagesId, Hotkey.Parse(_settings.Current.HotkeyMessages))) HotkeyConflicts.Add(HotkeyMessagesId);
        LoggingService.Write(LogEvent.HotkeysRegistered, code: HotkeyConflicts.Count);
    }

    internal void HotkeyPressed(int id)
    {
        if (id == HotkeyShowWindowId) ToggleMainWindow();
        else if (id == HotkeyMessagesId) _ = RunActionAsync(_web.ToggleMessagesWindowAsync);
    }

    // In front: back to the tray. Hidden, minimized or behind: to the front.
    internal void ToggleMainWindow()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive) HideToTray(showHint: false);
        else ShowFromTray();
    }

    // Ctrl+/ or F1.
    private ShortcutsWindow? _shortcutsWindow;
    internal ShortcutsWindow ShowShortcuts()
    {
        if (_shortcutsWindow is { } open) { open.Activate(); return open; }
        var window = new ShortcutsWindow(_settings.Current) { Owner = IsVisible ? this : null };
        window.Closed += (_, _) => _shortcutsWindow = null;
        _shortcutsWindow = window;
        window.Show();
        return window;
    }

    // A taskbar jump list entry (or the same command-line argument).
    internal void RunCommand(AppCommand command)
    {
        if (_disposed) return;
        switch (command)
        {
            case AppCommand.Home or AppCommand.Messages or AppCommand.Reels or AppCommand.Explore:
                ShowFromTray();
                var section = command switch
                {
                    AppCommand.Messages => NavigationSection.Messages, AppCommand.Reels => NavigationSection.Reels,
                    AppCommand.Explore => NavigationSection.Explore, _ => NavigationSection.Home
                };
                _ = RunActionAsync(() => _web.NavigateSectionAsync(section));
                break;
            case AppCommand.MessagesWindow:
                _ = RunActionAsync(_web.OpenMessagesWindowAsync);
                break;
            case AppCommand.Pause1h:
                var until = DateTimeOffset.Now.AddHours(1);
                _ = PauseNotificationsAsync(until);
                ShowBalloon(Loc.T("Main.PausedTitle"), Loc.F("Main.PausedUntil", FormatTime(until)), null, milliseconds: 3000);
                break;
            case AppCommand.Resume:
                _ = PauseNotificationsAsync(null);
                ShowBalloon(Loc.T("Main.Resumed"), Loc.T("Main.ResumedText"), null, milliseconds: 3000);
                break;
        }
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
            ShowBalloon(Loc.T("Main.TrayHintTitle"), Loc.T("Main.TrayHintText"), null, milliseconds: 3000);
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

    // Starts a new instance that waits for this one to release the
    // single-instance lock, then exits normally.
    internal async Task RestartAsync()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!,
                App.RelaunchArgument + (IsVisible ? "" : " --background")) { UseShellExecute = false });
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            MessageBox.Show(this, Loc.T("Main.RestartFailed"), "InstaDesktop");
            return;
        }
        await ExitAsync();
    }

    // An update may restart the app only while it is out of the way.
    internal bool IsIdleForUpdate => !_disposed && !_exiting && (!IsVisible || WindowState == WindowState.Minimized) &&
        _settingsWindow is null && _web.CallWindows.Count == 0 && _web.MessagesWindow is not { IsVisible: true };

    internal async Task SavePlacementAsync()
    {
        if (_mediaFullscreen) return;
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        bool maximized = _lastMaximized;
        try
        {
            await _settings.UpdateAsync(settings =>
            {
                if (!bounds.IsEmpty)
                {
                    settings.Left = bounds.Left; settings.Top = bounds.Top;
                    settings.Width = bounds.Width; settings.Height = bounds.Height;
                }
                settings.Maximized = maximized;
            });
        }
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
        if (MaximizeButton is not null) MaximizeButton.ToolTip = Loc.T(WindowState == WindowState.Maximized ? "Main.Restore" : "Main.Maximize");
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
        if (message == GlobalHotkeys.WmHotkey)
        {
            int id = wParam.ToInt32();
            Dispatcher.BeginInvoke(new Action(() => HotkeyPressed(id)));
            handled = true;
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
        else if ((key is Key.OemQuestion && modifiers == ModifierKeys.Control) || (key == Key.F1 && modifiers == ModifierKeys.None))
            action = () => { ShowShortcuts(); return Task.CompletedTask; };
        else if (key == Key.OemComma && modifiers == ModifierKeys.Control)
            action = () => { ShowSettings(); return Task.CompletedTask; };
        else if (key == Key.F12 && _settings.Current.DeveloperTools)
            action = () => { _web.Core?.OpenDevToolsWindow(); return Task.CompletedTask; };
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.M)
            action = _web.ToggleMessagesWindowAsync;
        else if (modifiers == ModifierKeys.Control && key >= Key.D1 && key <= Key.D4)
            action = () => _web.NavigateSectionAsync((NavigationSection)(key - Key.D1));
        else if ((modifiers & ~ModifierKeys.Shift) == ModifierKeys.Control && key is Key.OemPlus or Key.Add)
            action = () => { _web.StepZoom(+1); return Task.CompletedTask; };
        else if ((modifiers & ~ModifierKeys.Shift) == ModifierKeys.Control && key is Key.OemMinus or Key.Subtract)
            action = () => { _web.StepZoom(-1); return Task.CompletedTask; };
        else if (modifiers == ModifierKeys.Control && key is Key.D0 or Key.NumPad0)
            action = () => { _web.ResetZoom(); return Task.CompletedTask; };
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
        ThemeService.WindowsThemeChanged -= WindowsThemeChanged;
        _hotkeys?.Dispose();
        _pauseTimer?.Stop();
        Notifications.Dispose();
        _web.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        DisposeTrayBadge();
    }

    private void ShowZoom(double factor)
    {
        int percent = (int)Math.Round(factor * 100);
        ZoomText.Text = percent + "%";
        ZoomButton.Visibility = percent == 100 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _web.ResetZoom();

    // Right-click > Download photo / video finished.
    private void ShowMediaDownload(MediaDownloadResult result, bool video)
    {
        switch (result.Outcome)
        {
            case MediaDownloadOutcome.Saved when result.Path is { } path:
                ShowBalloon(Loc.F("Media.Saved", System.IO.Path.GetFileName(path)), Loc.T("Media.SavedText"), () => ShellService.ShowInFolder(path));
                break;
            case MediaDownloadOutcome.VideoUnavailable:
                ShowBalloon("Instagram", Loc.T("Media.VideoUnavailable"), null, Forms.ToolTipIcon.Info);
                break;
            case MediaDownloadOutcome.Failed:
                ShowBalloon("Instagram", Loc.T("Media.Failed"), null, Forms.ToolTipIcon.Warning);
                break;
        }
    }

    private void ShowNotice(string message) => ShowBalloon("Instagram", message.Replace('\n', ' '), null, milliseconds: 5000);
    private async void MessagesWindow_Click(object sender, RoutedEventArgs e) => await RunActionAsync(_web.ToggleMessagesWindowAsync);

    // Settings saved (or a background field changed): refresh what they drive.
    private void SettingsChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(SettingsChanged)); return; }
        if (_disposed) return;
        UpdateUnreadBadge();
        SchedulePauseEnd();
        if (_hotkeys is not null) ApplyHotkeys();
    }

    // Low-key and actionable: one tray notice (throttled by WebViewService),
    // clicking it opens Settings. No modal dialog interrupts the call screen.
    private void ShowMediaBlocked(Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind kind)
    {
        bool camera = kind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Camera;
        ShowBalloon(Loc.T("Main.CallBlockedTitle"), Loc.T(camera ? "Main.CallBlockedCamera" : "Main.CallBlockedMicrophone"),
            ShowSettings, milliseconds: 8000);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
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
        TitleRow.Height = new GridLength(fullscreen ? 0 : 40);
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
