using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using InstaDesktop.Localization;
using InstaDesktop.Models;

namespace InstaDesktop.Services;

public enum PermissionResetResult { Reset, NotReady, Unsupported, Failed }

public sealed class WebViewService : IDisposable
{
    private readonly Grid _host;
    private readonly Window _owner;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private bool _disposed;
    private bool _memoryApiAvailable = true;
    private bool _background;
    private string? _notificationScriptId;
    private string? _pendingNotificationThread;
    private readonly NotificationService _notifications;
    private DirectInboxMonitor? _directMonitor;
    private readonly DispatcherTimer _directMonitorCheck;
    private bool _checkingDirectMonitor, _diagnosticInitialization;
    private DateTimeOffset _nextDirectMonitorAttempt;
    private CoreWebView2MemoryUsageTargetLevel? _memoryTarget;
    private readonly DispatcherTimer _recoveryTimer, _zoomSave;
    private int _recoveryAttempt;
    private ulong _currentNavigationId;
    private bool _pageLoaded;
    private bool _callWindowsOnTop;
    private bool _canAutoRecover;
    // Diagnostics only: lets the isolated notification runner exercise the real
    // monitor lifecycle against offline fixtures. Never set in normal mode.
    internal Action<CoreWebView2>? DiagnosticMonitorConfigure { get; set; }
    internal DirectInboxMonitor? DirectMonitor => _directMonitor;
    // Diagnostics only: extra Chromium switches (virtual capture devices) for an
    // isolated diagnostic environment, and an automated answer in place of the
    // permission MessageBox. Both are ignored unless set by a diagnostic runner.
    internal static string? DiagnosticBrowserArguments { get; set; }
    internal Action<CoreWebView2>? DiagnosticCallConfigure { get; set; }
    internal Func<IReadOnlyList<CoreWebView2PermissionKind>, string, Task<bool>>? DiagnosticPermissionPrompt { get; set; }
    internal int PendingPermissionCount => _pendingPermissions.Count(p => !p.Completed);
    private readonly SemaphoreSlim _mediaSync = new(1, 1);
    private readonly List<PendingPermission> _pendingPermissions = new();
    private readonly Dictionary<CoreWebView2PermissionKind, bool> _appliedMedia = new();
    private readonly Dictionary<CoreWebView2PermissionKind, int> _mediaGeneration = new();
    private readonly HashSet<CoreWebView2PermissionKind> _clearDeniedMedia = new();
    private readonly Dictionary<CoreWebView2PermissionKind, DateTimeOffset> _lastBlockedNotice = new();
    private bool _prompting;
    // Live views (main page and call windows) and their document generation.
    private readonly Dictionary<CoreWebView2, int> _documentEpochs = new();
    private readonly List<CallWindow> _callWindows = new();
    internal IReadOnlyList<CallWindow> CallWindows => _callWindows;
    private IEnumerable<PopoutWindow> Popouts => _messagesWindow is { } messages ? _callWindows.Append<PopoutWindow>(messages) : _callWindows;
    internal static TimeSpan BlockedNoticeInterval { get; set; } = TimeSpan.FromSeconds(60);
    // Raised (throttled) when a call is blocked by a desktop switch that is off.
    public event Action<CoreWebView2PermissionKind>? MediaBlocked;
    private bool MonitorAllowed => _settings.Current.AppNotifications &&
        (!_diagnosticInitialization || DiagnosticMonitorConfigure is not null);
    public WebView2? View { get; private set; }
    // After the browser process exits, the WPF control throws on CoreWebView2;
    // keep our own reference so recovery can still tear the old view down.
    private CoreWebView2? _primaryCore;
    public CoreWebView2? Core => _primaryCore;
    public bool NeedsRecovery { get; private set; }
    // The failure is a missing WebView2 Runtime (the status offers its download).
    public bool RuntimeMissing { get; private set; }
    public bool MemoryApiAvailable => _memoryApiAvailable;
    public string? LastFailure { get; private set; }
    public event Action<string, bool>? StatusChanged;
    public event Action? Ready;
    public event Action? ShowRequested;
    public event Action? CloseRequested;
    public event Action<bool>? FullscreenChanged;
    public event Action<string>? UserNotice;
    public event Action<double>? ZoomChanged;
    // Instagram's Messages unread count (0 when signed out).
    public event Action<int>? UnreadCountChanged;
    public int UnreadCount { get; private set; }
    public NavigationSection CurrentSection { get; private set; } = NavigationSection.Home;

    public WebViewService(Grid host, Window owner, SettingsService settings, NotificationService notifications)
    {
        _host = host;
        _owner = owner;
        _settings = settings;
        _notifications = notifications;
        _directMonitorCheck = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            async (_, _) => await RefreshDirectMonitorAsync(), owner.Dispatcher);
        _directMonitorCheck.Stop();
        _callWindowsOnTop = settings.Current.CallWindowsOnTop;
        _recoveryTimer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher);
        _recoveryTimer.Tick += async (_, _) => await RecoverAsync();
        _zoomSave = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background,
            async (_, _) => await SaveZoomAsync(), owner.Dispatcher);
        _zoomSave.Stop();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        ThemeService.Changed += ApplyTheme;
        foreach (var kind in MediaPermissionPolicy.Kinds)
        {
            _appliedMedia[kind] = MediaPermissionPolicy.Enabled(settings.Current, kind);
            _mediaGeneration[kind] = 0;
        }
    }

    public async Task InitializeAsync(Action<CoreWebView2>? diagnosticConfigure = null)
    {
        await _operation.WaitAsync();
        try
        {
            if (_disposed) return;
            _recoveryTimer.Stop();
            _pageLoaded = false;
            DisposeView();
            _diagnosticInitialization = diagnosticConfigure is not null;
            NeedsRecovery = false;
            LastFailure = null;
            _memoryApiAvailable = true;
            _memoryTarget = null;
            RuntimeMissing = false;
            StatusChanged?.Invoke(Loc.T("Main.Loading"), false);
            Directory.CreateDirectory(AppPaths.UserData);
            string arguments = _settings.Current.HardwareAcceleration ? "" : "--disable-gpu";
            if (_diagnosticInitialization && DiagnosticBrowserArguments is { } diagnosticArguments)
                arguments = (arguments + " " + diagnosticArguments).Trim();
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = arguments };
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.UserData, options: options)
                .WaitAsync(TimeSpan.FromSeconds(30));
            if (_disposed) return;
            View = new WebView2 { DefaultBackgroundColor = ThemeService.WebBackground, ZoomFactor = _settings.Current.ZoomFactor };
            View.ZoomFactorChanged += ViewZoomFactorChanged;
            _host.Children.Add(View);
            await View.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
            if (_disposed) return;
            _primaryCore = View.CoreWebView2;
            diagnosticConfigure?.Invoke(View.CoreWebView2);
            await ConfigureCoreAsync(View.CoreWebView2);
            // Before the first navigation: the page must never see a stale grant.
            await SyncMediaPermissionsAsync(View.CoreWebView2, startup: true);
            if (_disposed) return;
            _notifications.DirectInboxMonitoring = MonitorAllowed;
            if (_notifications.DirectInboxMonitoring) _directMonitorCheck.Start();
            await EnsureNotificationPermissionAsync(View.CoreWebView2);
            SetBackground(_background);
            View.CoreWebView2.Navigate(_pendingNotificationThread ?? NavigationPolicy.Home);
            _pendingNotificationThread = null;
        }
        catch (Exception e)
        {
            if (_disposed) return;
            LoggingService.Write(LogEvent.WebViewInitializationError, e);
            bool runtimeMissing = e is WebView2RuntimeNotFoundException;
            RuntimeMissing = runtimeMissing;
            Fail(Loc.T(runtimeMissing ? "Web.RuntimeRequired" : "Web.InitFailed"), retry: !runtimeMissing);
        }
        finally { _operation.Release(); }
    }

    private async Task EnsureNotificationPermissionAsync(CoreWebView2 core)
    {
        // Instagram uses the Web Notifications API for message alerts. A
        // previous denial is persisted in the WebView2 profile, so explicitly
        // synchronize the two expected origins with the app's notification setting.
        // The hidden inbox monitor shares this profile and relies on this state.
        try
        {
            await NotificationPermissionPolicy.SyncAsync(core.Profile, () => _settings.Current.AppNotifications);
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.NotificationInitializationFailed, error);
        }
    }

    private async Task ConfigureCoreAsync(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled = _settings.Current.DeveloperTools;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = true;
        // Let Instagram/WebView handle its native keyboard and Emoji input.
        s.AreBrowserAcceleratorKeysEnabled = true;
        s.AreHostObjectsAllowed = false;
        // Keep built-in editing, media, spellcheck and file-picker behavior.
        s.AreDefaultContextMenusEnabled = true;
        core.NavigationStarting += NavigationStarting;
        core.NavigationCompleted += NavigationCompleted;
        core.NewWindowRequested += NewWindowRequested;
        core.PermissionRequested += PermissionRequested;
        // A new document invalidates any permission question asked by the old one.
        _documentEpochs[core] = 0;
        core.ContentLoading += (_, _) => { if (_documentEpochs.ContainsKey(core)) _documentEpochs[core]++; };
        core.DownloadStarting += DownloadStarting;
        core.ContextMenuRequested += ContextMenuRequested;
        core.ProcessFailed += ProcessFailed;
        core.WebMessageReceived += WebMessageReceived;
        core.SourceChanged += SourceChanged;
        core.WindowCloseRequested += WindowCloseRequested;
        core.HistoryChanged += (_, _) => PublishNavigationState();
        ZoomChanged?.Invoke(View?.ZoomFactor ?? 1);
        core.ContainsFullScreenElementChanged += (_, _) => FullscreenChanged?.Invoke(core.ContainsFullScreenElement);
        ApplyColorScheme(core);
        ApplyDownloadFolder(core);
        try { core.NotificationReceived += NotificationReceived; }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationInitializationFailed, error); }
        try { await EmojiFontService.ConfigureAsync(core); }
        catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(ViewportStyleScript); }
        catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
        await ConfigureNotificationsAsync(core);
    }

    // Keeps Instagram's original UI while preventing WebView scrollbars from
    // painting a white gutter over the dark page. Applied before first paint
    // instead of after every completed load.
    // Light or dark for Instagram (prefers-color-scheme; Instagram's own
    // appearance choice still wins) and for what WebView2 paints before it.
    private void ApplyTheme()
    {
        if (_disposed) return;
        if (View is { } view) view.DefaultBackgroundColor = ThemeService.WebBackground;
        if (_messagesWindow is { } messages) messages.View.DefaultBackgroundColor = ThemeService.WebBackground;
        if (Core is { } core && !NeedsRecovery) ApplyColorScheme(core);
    }

    private static void ApplyColorScheme(CoreWebView2 core)
    {
        try
        {
            core.Profile.PreferredColorScheme = ThemeService.IsDark
                ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }
        catch (Exception e) when (e is NotImplementedException or COMException or InvalidOperationException) { }
    }

    private const string ViewportStyleScript = """
        (() => {
          if (window !== window.top || !/(^|\.)instagram\.com$/i.test(location.hostname)) return;
          const css = 'html,body{overflow-x:clip!important;max-width:100vw!important;}' +
            '@media (prefers-color-scheme: dark){html,body{background:#000!important;}}' +
            'html{scrollbar-width:none!important;}' +
            'html::-webkit-scrollbar,body::-webkit-scrollbar{display:none!important;width:0!important;height:0!important;}';
          const add = () => {
            if (!document.documentElement || document.getElementById('instadesktop-viewport-style')) return;
            const style = document.createElement('style');
            style.id = 'instadesktop-viewport-style';
            style.textContent = css;
            (document.head || document.documentElement).appendChild(style);
          };
          add();
          document.addEventListener('DOMContentLoaded', add, { once: true });
          window.addEventListener('load', add, { once: true });
        })();
        """;

    private async Task ConfigureNotificationsAsync(CoreWebView2 core)
    {
        try
        {
            // Use the embedded bridge so upgrades cannot leave an obsolete copy
            // behind when the installer preserves user-editable customization.
            string script = "window.__InstaDesktopNotificationsEnabled = " +
                (_settings.Current.AppNotifications ? "true;\n" : "false;\n") +
                await ReadNotificationAssetAsync("direct-inbox-dom.js") + "\n" + await ReadNotificationAssetAsync("notifications.js") +
                "\n" + await ReadNotificationAssetAsync("unread-count.js");
            string next = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            if (_notificationScriptId is not null) core.RemoveScriptToExecuteOnDocumentCreated(_notificationScriptId);
            _notificationScriptId = next;
            if (NotificationPolicy.IsInstagramOrigin(core.Source))
            {
                await core.ExecuteScriptAsync(script);
                await core.ExecuteScriptAsync("window.__InstaDesktopNotifications?.setEnabled(window.__InstaDesktopNotificationsEnabled);");
            }
        }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationInitializationFailed, error); }
    }

    internal static async Task<string> ReadNotificationAssetAsync(string name)
    {
        using var stream = typeof(WebViewService).Assembly.GetManifestResourceStream("InstaDesktop.Assets.Scripts." + name)
            ?? throw new FileNotFoundException();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private void NotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        // Always mark handled, including disabled mode: don't leak duplicate browser UI.
        e.Handled = true;
        LoggingService.Write(LogEvent.NotificationReceived, code: 1);
        if (_disposed || !_settings.Current.AppNotifications || !NotificationPolicy.IsInstagramOrigin(e.SenderOrigin)) return;
        _ = RefreshDirectMonitorAsync();
        if (_directMonitor is { } monitor) _ = monitor.RefreshSnapshotAsync();
        ForwardNativeNotification(_notifications, e, PrimaryEmitter, sender ?? this);
    }

    internal const string PrimaryEmitter = "primary";

    // Both controllers use the same payload mapping and native lifecycle owner.
    // The hidden inbox can be the only page that creates an actual notification.
    // The emitter lets the coordinator collapse one message mirrored by both pages.
    internal static void ForwardNativeNotification(NotificationService notifications, CoreWebView2NotificationReceivedEventArgs e, string emitter, object owner)
    {
        e.Handled = true;
        if (!NotificationPolicy.IsInstagramOrigin(e.SenderOrigin)) return;
        try
        {
            var native = e.Notification;
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            bool hasTimestamp = false;
            if (native.Timestamp > DateTime.UnixEpoch)
            {
                timestamp = new DateTimeOffset(native.Timestamp.ToUniversalTime());
                hasTimestamp = true;
            }
            // Notification data/click URLs aren't exposed by this SDK. Only use a
            // tag as a route when it is itself an exact, validated Direct URL.
            string? thread = NotificationPolicy.DirectUrl(native.Tag);
            notifications.Receive(new InstagramNotification
            {
                Source = NotificationSource.NativeWebView,
                Type = thread is null ? InstagramNotificationType.Instagram : InstagramNotificationType.DirectMessage,
                Title = native.Title, Body = native.Body, Tag = native.Tag,
                AvatarUrl = native.IconUri, ImageUrl = native.BodyImageUri,
                ThreadUrl = thread, Origin = new Uri(e.SenderOrigin).GetLeftPart(UriPartial.Authority),
                Timestamp = timestamp, HasSourceTimestamp = hasTimestamp, Silent = native.IsSilent,
                Emitter = emitter
            }, native, owner);
        }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationInitializationFailed, error); }
    }
    private void SetUnreadCount(int count)
    {
        if (UnreadCount == count) return;
        UnreadCount = count;
        UnreadCountChanged?.Invoke(count);
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsSessionRoute(e.Uri))
        {
            StopDirectMonitor();
            SetUnreadCount(0); // signing in or out: the old account's count no longer applies
        }
        if (NavigationPolicy.IsTrusted(e.Uri))
        {
            _currentNavigationId = e.NavigationId;
            return;
        }
        e.Cancel = true;
        string? upgraded = NavigationPolicy.UpgradeInstagramHttp(e.Uri);
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (upgraded is not null) Core?.Navigate(upgraded);
            else ShellService.OpenWeb(e.Uri);
        }));
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed) return;
        if (!e.IsSuccess)
        {
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            LoggingService.Write(LogEvent.NavigationError, code: (int)e.WebErrorStatus);
            // A load replaced by a newer one (fast back/forward, notification
            // click) or stopped by the page ends as ConnectionAborted. The newer
            // load decides, and an already loaded page stays usable.
            if (e.NavigationId != _currentNavigationId ||
                (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted && _pageLoaded)) return;
            Fail(Loc.T("Web.LoadFailed"));
            return;
        }
        NeedsRecovery = false;
        LastFailure = null;
        _pageLoaded = true;
        _recoveryAttempt = 0;
        _recoveryTimer.Stop();
        if (View is not null) View.Visibility = Visibility.Visible;
        Ready?.Invoke();
        _ = RefreshDirectMonitorAsync();
        PublishNavigationState();
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        string uri = e.Uri;
        // Fixed origin/path classes only, never the URL. Code 121 (www, "call"
        // path, user gesture) is how Instagram opens its call page.
        LoggingService.Write(LogEvent.NewWindowRequested,
            code: MediaPermissionPolicy.OriginCategory(uri) * 100 + PathCategory(uri) * 10 + (e.IsUserInitiated ? 1 : 0));
        if (!_disposed && sender is CoreWebView2 opener && IsCallPopup(uri))
        {
            OpenCallWindow(opener, e);
            return;
        }
        e.Handled = true;
        bool fromPopout = sender is CoreWebView2 source && source != Core;
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (fromPopout && (NavigationPolicy.IsTrusted(uri) || NavigationPolicy.UpgradeInstagramHttp(uri) is not null)) ShowRequested?.Invoke();
            if (NavigationPolicy.IsTrusted(uri)) Core?.Navigate(uri);
            else if (NavigationPolicy.UpgradeInstagramHttp(uri) is { } https) Core?.Navigate(https);
            else ShellService.OpenWeb(uri);
        }));
    }

    // Only a call page on the two exact media origins gets its own window;
    // other Instagram pop-ups keep opening in the main view.
    internal static bool IsCallPopup(string? uri) => MediaPermissionPolicy.IsMediaOrigin(uri) && PathCategory(uri) == 2;

    // Real pop-up semantics (SDK NewWindow): same environment and profile as
    // the opener, target not navigated before assignment, so window.opener,
    // postMessage and window.close() behave as in a browser. The main window
    // stays usable during the call.
    private void OpenCallWindow(CoreWebView2 opener, CoreWebView2NewWindowRequestedEventArgs e)
    {
        CoreWebView2Deferral deferral;
        try { deferral = e.GetDeferral(); }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            LoggingService.Write(LogEvent.CallWindowFailed, error);
            e.Handled = true;
            return;
        }
        double width = 1000, height = 720;
        try
        {
            var features = e.WindowFeatures;
            if (features.HasSize) { width = features.Width; height = features.Height; }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotImplementedException) { }
        var environment = opener.Environment;
        _owner.Dispatcher.BeginInvoke(new Action(async () =>
        {
            CallWindow? window = null;
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WebViewService));
                window = new CallWindow(width, height, _owner, _settings.Current.CallWindowsOnTop);
                window.Show();
                await window.View.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20));
                if (_disposed) throw new ObjectDisposedException(nameof(WebViewService));
                var core = window.View.CoreWebView2;
                ConfigureCallCore(core, window);
                DiagnosticCallConfigure?.Invoke(core);
                e.NewWindow = core;
                e.Handled = true;
                _callWindows.Add(window);
                LoggingService.Write(LogEvent.CallWindowOpened, code: _callWindows.Count);
                SetBackground(_background);
            }
            catch (Exception error)
            {
                LoggingService.Write(LogEvent.CallWindowFailed, error);
                try { e.Handled = true; }
                catch (Exception) { }
                if (window is not null) { _callWindows.Remove(window); window.Close(); }
                if (!_disposed) UserNotice?.Invoke(Loc.T("Web.CallWindowFailed"));
            }
            finally
            {
                try { deferral.Complete(); }
                catch (Exception error) when (error is COMException or InvalidOperationException) { LoggingService.Write(LogEvent.CallWindowFailed, error); }
            }
        }));
    }

    // Shared by call windows and the messages window: same permission flow,
    // downloads and pop-up handling as the main page; the main page owns
    // notifications. Window-specific cleanup is registered by the caller.
    private void ConfigurePopoutCore(CoreWebView2 core, PopoutWindow window, bool call)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled = _settings.Current.DeveloperTools;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = !call;
        s.AreHostObjectsAllowed = false;
        s.AreBrowserAcceleratorKeysEnabled = true;
        s.AreDefaultContextMenusEnabled = true;
        _documentEpochs[core] = 0;
        core.ContentLoading += (_, _) => { if (_documentEpochs.ContainsKey(core)) _documentEpochs[core]++; };
        // Same media rules, prompts and revocation as the main page.
        core.PermissionRequested += PermissionRequested;
        core.NewWindowRequested += NewWindowRequested;
        core.DownloadStarting += DownloadStarting;
        core.NotificationReceived += (_, e) => e.Handled = true; // the main page owns notifications
        core.NavigationStarting += (_, e) =>
        {
            if (NavigationPolicy.IsTrusted(e.Uri)) return;
            e.Cancel = true;
            string target = e.Uri;
            _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (NavigationPolicy.UpgradeInstagramHttp(target) is { } https) { if (_documentEpochs.ContainsKey(core)) core.Navigate(https); }
                else ShellService.OpenWeb(target);
            }));
        };
        // A call window shows the call page's title; the messages panel keeps its own.
        string fallbackTitle = window.Title;
        if (call)
            core.DocumentTitleChanged += (_, _) =>
                window.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? fallbackTitle : core.DocumentTitle;
        core.ContainsFullScreenElementChanged += (_, _) => window.SetFullscreen(core.ContainsFullScreenElement);
        // The page ending itself (a call hanging up) closes only its own window.
        core.WindowCloseRequested += (_, _) =>
        {
            LoggingService.Write(LogEvent.WindowCloseRequested, code: 10 + PathCategory(core.Source));
            _owner.Dispatcher.BeginInvoke(new Action(window.Close));
        };
        core.ProcessFailed += (_, e) =>
        {
            LoggingService.Write(LogEvent.WebViewProcessError, code: 2000 + (int)e.ProcessFailedKind);
            void ClosePopout()
            {
                if (!_documentEpochs.ContainsKey(core)) return; // already closed
                CancelPendingPermissions(core, null, MediaPermissionPolicy.Reason.Canceled);
                _owner.Dispatcher.BeginInvoke(new Action(window.CloseForGood));
            }
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                _ = ReplaceIfStillUnresponsiveAsync(core, ClosePopout);
            else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited)
                ClosePopout();
        };
        window.Closed += (_, _) =>
        {
            CancelPendingPermissions(core, null, MediaPermissionPolicy.Reason.Canceled);
            _documentEpochs.Remove(core);
            try { window.View.Dispose(); }
            catch (Exception error) { LoggingService.Write(call ? LogEvent.CallWindowFailed : LogEvent.MessagesWindowFailed, error); }
        };
    }

    private void ConfigureCallCore(CoreWebView2 core, CallWindow window)
    {
        ConfigurePopoutCore(core, window, call: true);
        window.Closed += (_, _) =>
        {
            _callWindows.Remove(window);
            LoggingService.Write(LogEvent.CallWindowClosed, code: _callWindows.Count);
            SetBackground(_background);
        };
    }

    // ---- Messages window ----------------------------------------------------

    private MessagesWindow? _messagesWindow;
    internal MessagesWindow? MessagesWindow => _messagesWindow;

    // A hidden panel keeps its page for an instant return, but not forever.
    internal static TimeSpan MessagesIdleClose { get; set; } = TimeSpan.FromMinutes(10);
    private DispatcherTimer? _messagesIdle;

    // Title bar button, Ctrl+Shift+M and the tray: hide the panel when it is
    // in front, otherwise show it.
    public async Task<bool> ToggleMessagesWindowAsync()
    {
        if (_messagesWindow is { IsVisible: true, IsActive: true } shown)
        {
            shown.Close(); // hides
            return true;
        }
        return await OpenMessagesWindowAsync();
    }

    // Opens (or brings back) the floating inbox panel. Needs the main page's
    // environment, so it waits until Instagram has started once.
    public async Task<bool> OpenMessagesWindowAsync()
    {
        if (_disposed) return false;
        if (_messagesWindow is { } open)
        {
            _messagesIdle?.Stop();
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Show();
            open.Activate();
            open.View.Focus();
            return true;
        }
        if (Core is not { } primary || NeedsRecovery)
        {
            UserNotice?.Invoke(Loc.T("Web.MessagesNotReady"));
            return false;
        }
        var s = _settings.Current;
        var window = new MessagesWindow(s.MessagesWidth, s.MessagesHeight, s.MessagesLeft, s.MessagesTop, _owner);
        _messagesWindow = window;
        Action<int> unread = window.SetUnread;
        window.SetUnread(UnreadCount);
        UnreadCountChanged += unread;
        window.OpenInMainRequested += () => OpenMessagesInMain(window);
        window.Hidden += () =>
        {
            _ = SaveMessagesBoundsAsync(window);
            _messagesIdle ??= new DispatcherTimer(MessagesIdleClose, DispatcherPriority.Background, (_, _) =>
            {
                _messagesIdle?.Stop();
                if (_messagesWindow is { IsVisible: false } idle) idle.CloseForGood();
            }, _owner.Dispatcher);
            _messagesIdle.Interval = MessagesIdleClose;
            _messagesIdle.Stop();
            _messagesIdle.Start();
        };
        window.Closed += async (_, _) =>
        {
            UnreadCountChanged -= unread;
            _messagesIdle?.Stop();
            if (_messagesWindow == window) _messagesWindow = null;
            LoggingService.Write(LogEvent.MessagesWindowClosed);
            SetBackground(_background);
            await SaveMessagesBoundsAsync(window);
        };
        try
        {
            window.Show();
            await window.View.EnsureCoreWebView2Async(primary.Environment).WaitAsync(TimeSpan.FromSeconds(20));
            if (_disposed || _messagesWindow != window) { window.Close(); return false; }
            var core = window.View.CoreWebView2;
            ConfigurePopoutCore(core, window, call: false);
            try { await EmojiFontService.ConfigureAsync(core); }
            catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ViewportStyleScript);
            core.Navigate(DirectInboxMonitor.InboxUrl);
            SetBackground(_background);
            LoggingService.Write(LogEvent.MessagesWindowOpened);
            return true;
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.MessagesWindowFailed, error);
            window.Close();
            return false;
        }
    }

    private async Task SaveMessagesBoundsAsync(MessagesWindow window)
    {
        var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
        if (_disposed || bounds.IsEmpty || !double.IsFinite(bounds.Left)) return;
        try
        {
            await _settings.UpdateAsync(x =>
            {
                x.MessagesLeft = bounds.Left; x.MessagesTop = bounds.Top;
                x.MessagesWidth = bounds.Width; x.MessagesHeight = bounds.Height;
            });
        }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    // The panel's "open in main window": the same conversation, full size.
    private void OpenMessagesInMain(MessagesWindow window)
    {
        string? source = window.View.CoreWebView2?.Source;
        string target = NavigationPolicy.IsTrusted(source) ? source! : DirectInboxMonitor.InboxUrl;
        ShowRequested?.Invoke();
        if (Core is { } core && !NeedsRecovery) core.Navigate(target);
        window.Close(); // hides
    }

    private void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (MediaPermissionPolicy.IsMediaKind(e.PermissionKind))
        {
            MediaPermissionRequested(sender as CoreWebView2, e);
            return;
        }
        if (!NavigationPolicy.IsTrusted(e.Uri))
        {
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }
        if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
        {
            // Never a dialog: notifications follow the desktop setting only.
            var decision = NotificationPermissionPolicy.ForPrimaryNotifications(e.Uri, _settings.Current.AppNotifications);
            e.State = decision.State;
            e.SavesInProfile = decision.SavesInProfile;
            LoggingService.Write(LogEvent.NotificationPermission, code: 10 + (int)e.State);
            return;
        }
        EnqueuePermission(sender as CoreWebView2, e);
    }

    private void MediaPermissionRequested(CoreWebView2? core, CoreWebView2PermissionRequestedEventArgs e)
    {
        var kind = e.PermissionKind;
        var decision = MediaPermissionPolicy.ForPrimary(kind, e.Uri, !_disposed && MediaPermissionPolicy.Enabled(_settings.Current, kind));
        if (decision.Prompt)
        {
            EnqueuePermission(core, e);
            return;
        }
        // Request-only: a saved app denial would suppress every later event,
        // including after the user turns the switch on.
        PendingPermission.Answer(e, CoreWebView2PermissionState.Deny, saves: false);
        LoggingService.Write(LogEvent.MediaPermission, code: MediaPermissionPolicy.LogCode(1, kind, e.Uri, decision.Reason,
            CoreWebView2PermissionState.Deny, false));
        if (decision.Reason == MediaPermissionPolicy.Reason.SettingOff) NotifyMediaBlocked(kind);
    }

    private void NotifyMediaBlocked(CoreWebView2PermissionKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastBlockedNotice.TryGetValue(kind, out var last) && now - last < BlockedNoticeInterval) return;
        _lastBlockedNotice[kind] = now;
        LoggingService.Write(LogEvent.MediaPermissionBlockedNotice, code: (int)kind);
        _owner.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) MediaBlocked?.Invoke(kind); }));
    }

    // One question per origin at a time. Every deferral taken here is completed
    // exactly once: by the user's answer, or with a request-only Deny when the
    // request became stale (setting off, new document, replaced or crashed
    // WebView, shutdown) or anything failed. It never completes as Default,
    // which would fall back to the WebView2 built-in permission UI.
    private sealed class PendingPermission
    {
        public required CoreWebView2PermissionRequestedEventArgs Args { get; init; }
        public required CoreWebView2Deferral Deferral { get; init; }
        public required CoreWebView2 Core { get; init; }
        public required CoreWebView2PermissionKind Kind { get; init; }
        public required string Uri { get; init; }
        public required string Origin { get; init; }
        public required int Generation { get; init; }
        public required int Epoch { get; init; }
        public DateTime Created { get; } = DateTime.UtcNow;
        public bool Completed { get; private set; }

        public static void Answer(CoreWebView2PermissionRequestedEventArgs args, CoreWebView2PermissionState state, bool saves)
        {
            try { args.SavesInProfile = saves; }
            catch (Exception error) when (error is NotImplementedException or COMException or InvalidOperationException) { }
            try { args.State = state; }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            { LoggingService.Write(LogEvent.MediaPermissionCompletionFailed, error); }
        }

        public void Complete(CoreWebView2PermissionState state, bool saves, MediaPermissionPolicy.Reason reason)
        {
            if (Completed) return;
            Completed = true;
            Answer(Args, state, saves);
            try { Deferral.Complete(); }
            catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException)
            { LoggingService.Write(LogEvent.MediaPermissionCompletionFailed, error); }
            if (MediaPermissionPolicy.IsMediaKind(Kind))
                LoggingService.Write(LogEvent.MediaPermission, code: MediaPermissionPolicy.LogCode(1, Kind, Uri, reason, state, saves));
        }
    }

    private void EnqueuePermission(CoreWebView2? core, CoreWebView2PermissionRequestedEventArgs e)
    {
        CoreWebView2Deferral deferral;
        try { deferral = e.GetDeferral(); }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            LoggingService.Write(LogEvent.MediaPermissionCompletionFailed, error);
            PendingPermission.Answer(e, CoreWebView2PermissionState.Deny, saves: false);
            return;
        }
        string origin = Uri.TryCreate(e.Uri, UriKind.Absolute, out var parsed) ? parsed.GetLeftPart(UriPartial.Authority) : "";
        var request = new PendingPermission
        {
            Args = e, Deferral = deferral, Core = core ?? Core!, Kind = e.PermissionKind, Uri = e.Uri, Origin = origin,
            Generation = _mediaGeneration.GetValueOrDefault(e.PermissionKind),
            Epoch = core is not null && _documentEpochs.TryGetValue(core, out int epoch) ? epoch : -1
        };
        if (_disposed || core is null || request.Epoch < 0 || _owner.Dispatcher.HasShutdownStarted)
        {
            request.Complete(CoreWebView2PermissionState.Deny, false, MediaPermissionPolicy.Reason.Stale);
            return;
        }
        _pendingPermissions.Add(request);
        if (MediaPermissionPolicy.IsMediaKind(request.Kind))
            LoggingService.Write(LogEvent.MediaPermission, code: MediaPermissionPolicy.LogCode(1, request.Kind, request.Uri,
                MediaPermissionPolicy.Reason.Prompt, CoreWebView2PermissionState.Default, false));
        // Defer modal UI until after the WebView callback returns (COM reentrancy).
        var operation = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ProcessPermissionPromptsAsync()));
        if (operation.Status == DispatcherOperationStatus.Aborted)
            CancelPendingPermissions(null, null, MediaPermissionPolicy.Reason.Failed);
    }

    private bool IsCurrent(PendingPermission request) =>
        !_disposed && _documentEpochs.TryGetValue(request.Core, out int epoch) && request.Epoch == epoch &&
        !(request.Core == Core && NeedsRecovery) &&
        (!MediaPermissionPolicy.IsMediaKind(request.Kind) ||
         (MediaPermissionPolicy.IsMediaOrigin(request.Uri) && MediaPermissionPolicy.Enabled(_settings.Current, request.Kind) &&
          _mediaGeneration.GetValueOrDefault(request.Kind) == request.Generation));

    private void CancelPendingPermissions(CoreWebView2? core, CoreWebView2PermissionKind? kind, MediaPermissionPolicy.Reason reason)
    {
        foreach (var request in _pendingPermissions.ToArray())
            if ((core is null || request.Core == core) && (kind is null || request.Kind == kind))
                request.Complete(CoreWebView2PermissionState.Deny, false, reason);
        _pendingPermissions.RemoveAll(p => p.Completed);
    }

    private async Task ProcessPermissionPromptsAsync()
    {
        if (_prompting) return;
        _prompting = true;
        try
        {
            while (_pendingPermissions.FirstOrDefault(p => !p.Completed) is { } first)
            {
                // getUserMedia({audio, video}) raises one event per device; let
                // the second arrive so one question covers the whole request.
                var wait = first.Created.AddMilliseconds(150) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                bool Same(PendingPermission p) => !p.Completed && p.Core == first.Core && p.Origin == first.Origin;
                foreach (var request in _pendingPermissions.Where(Same).ToArray())
                    if (!IsCurrent(request)) request.Complete(CoreWebView2PermissionState.Deny, false, MediaPermissionPolicy.Reason.Stale);
                var kinds = _pendingPermissions.Where(Same).Select(p => p.Kind).Distinct().ToList();
                if (kinds.Count > 0)
                {
                    // Ask over the window that asked: a call window, or the main window.
                    Window owner = Popouts.FirstOrDefault(w => w.View.CoreWebView2 == first.Core) ?? _owner;
                    if (owner == _owner) ShowRequested?.Invoke();
                    else owner.Activate();
                    LoggingService.Write(LogEvent.MediaPermissionPrompt, code: kinds.Sum(k => 1 << (int)k));
                    bool allow = await AskPermissionAsync(kinds, first.Uri, owner);
                    // Re-validate after the dialog: the switch, document or WebView
                    // may have changed while it was open. Requests of the same
                    // kinds that joined meanwhile share the answer.
                    foreach (var request in _pendingPermissions.Where(p => Same(p) && kinds.Contains(p.Kind)).ToArray())
                    {
                        if (!IsCurrent(request)) request.Complete(CoreWebView2PermissionState.Deny, false, MediaPermissionPolicy.Reason.Stale);
                        // Always saved: clearing a saved grant is what stops live capture.
                        else request.Complete(allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny, true,
                            allow ? MediaPermissionPolicy.Reason.UserAllowed : MediaPermissionPolicy.Reason.UserDenied);
                    }
                }
                _pendingPermissions.RemoveAll(p => p.Completed);
            }
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            CancelPendingPermissions(null, null, MediaPermissionPolicy.Reason.Failed);
        }
        finally { _prompting = false; }
    }

    private async Task<bool> AskPermissionAsync(IReadOnlyList<CoreWebView2PermissionKind> kinds, string uri, Window owner)
    {
        string host = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : "Instagram";
        if (DiagnosticPermissionPrompt is { } prompt) return await prompt(kinds, host);
        string names = string.Join(Loc.T("Web.PermissionAnd"), kinds.Select(kind => kind switch
        {
            CoreWebView2PermissionKind.Microphone => Loc.T("Web.PermissionMicrophone"),
            CoreWebView2PermissionKind.Camera => Loc.T("Web.PermissionCamera"),
            CoreWebView2PermissionKind.Notifications => Loc.T("Web.PermissionNotifications"),
            _ => kind.ToString()
        }));
        string detail = Loc.T(kinds.Any(MediaPermissionPolicy.IsMediaKind) ? "Web.PermissionRemember" : "Web.PermissionResetHint");
        return MessageBox.Show(owner, Loc.F("Web.PermissionQuestion", host, names) + Environment.NewLine + Environment.NewLine + detail,
            Loc.T("Web.PermissionTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public string DownloadFolder => _settings.Current.DownloadFolder ?? ShellService.DownloadsFolder;

    // Profile-wide, so call windows (same profile) save to the same place.
    private void ApplyDownloadFolder(CoreWebView2 core)
    {
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            core.Profile.DefaultDownloadFolderPath = DownloadFolder;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or COMException or NotImplementedException)
        { LoggingService.Write(LogEvent.DownloadFolderUnavailable, error); }
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        LoggingService.Write(LogEvent.DownloadStarted, code: _settings.Current.AskDownloadLocation ? 1 : 0);
        // Saved straight to the download folder: WebView2 names the file and
        // shows its own progress list; nothing else to decide here.
        if (!_settings.Current.AskDownloadLocation) return;
        var deferral = e.GetDeferral();
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_disposed) { e.Cancel = true; return; }
                ShowRequested?.Invoke();
                var dialog = new SaveFileDialog
                {
                    Title = Loc.T("Web.SaveDownload"),
                    InitialDirectory = Path.GetDirectoryName(e.ResultFilePath),
                    FileName = Path.GetFileName(e.ResultFilePath),
                    Filter = Loc.T("Web.AllFiles"),
                    OverwritePrompt = true
                };
                if (dialog.ShowDialog(_owner) == true) e.ResultFilePath = dialog.FileName;
                else e.Cancel = true;
                // Keep WebView2's download progress UI available after the save picker.
                e.Handled = false;
            }
            catch (Exception error)
            {
                e.Cancel = true;
                LoggingService.Write(LogEvent.UnexpectedException, error);
            }
            finally { deferral.Complete(); }
        }));
    }

    private static readonly HashSet<string> HiddenMenuItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "back", "forward", "reload", "saveAs", "print", "viewSource", "webSelect", "share",
        "openLinkInNewWindow", "openLinkInNewTab", "openLinkInInPrivateWindow"
    };

    private void ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        for (int i = e.MenuItems.Count - 1; i >= 0; i--)
        {
            string name = e.MenuItems[i].Name;
            if (HiddenMenuItems.Contains(name) || (!_settings.Current.DeveloperTools && name == "inspectElement"))
                e.MenuItems.RemoveAt(i);
        }
    }

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        LoggingService.Write(LogEvent.WebViewProcessError, code: (int)e.ProcessFailedKind);
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive && sender is CoreWebView2 hung)
        {
            _ = ReplaceIfStillUnresponsiveAsync(hung, () =>
            {
                if (_disposed || Core != hung || NeedsRecovery) return;
                LosePrimary(hung, Loc.T("Web.Unresponsive"));
            });
            return;
        }
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessExited)
            LosePrimary(sender, Loc.T("Web.Crashed"));
        // WebView2 automatically recovers ancillary GPU/utility processes.
    }

    private void LosePrimary(object? failed, string message)
    {
        if (failed is CoreWebView2 core) CancelPendingPermissions(core, null, MediaPermissionPolicy.Reason.Canceled);
        // The page that owned these native notifications is gone.
        if (failed is not null) _notifications.ReleaseNative(failed);
        Fail(message);
    }

    // "Unresponsive" is reported for a page that is only busy for a while
    // (large feed, video decode). Replacing it at once loses the page, or ends
    // a call; replace it only if it still cannot run a script after a grace period.
    internal static TimeSpan UnresponsiveGrace { get; set; } = TimeSpan.FromSeconds(20);
    private readonly HashSet<CoreWebView2> _probingUnresponsive = new();

    internal async Task ReplaceIfStillUnresponsiveAsync(CoreWebView2 core, Action replace)
    {
        if (!_probingUnresponsive.Add(core)) return;
        bool responsive;
        try
        {
            await core.ExecuteScriptAsync("0").WaitAsync(UnresponsiveGrace);
            responsive = true;
        }
        catch (Exception error) when (error is TimeoutException or COMException or InvalidOperationException or ObjectDisposedException)
        { responsive = false; }
        finally { _probingUnresponsive.Remove(core); }
        LoggingService.Write(LogEvent.RendererUnresponsive, code: responsive ? 0 : 1);
        if (!responsive) replace();
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!NavigationPolicy.IsTrusted(e.Source)) return;
        try
        {
            string json = e.WebMessageAsJson;
            if (json.Length > 16384) { LoggingService.Write(LogEvent.NotificationRejected); return; }
            if (NotificationPolicy.TryParse(e.Source, json, out var candidate))
            {
                if (_settings.Current.AppNotifications)
                {
                    if (candidate!.Source is NotificationSource.UnreadBadge or NotificationSource.PageNotification &&
                        _directMonitor is { } monitor) _ = monitor.RefreshSnapshotAsync();
                    _notifications.Receive(candidate);
                }
                return;
            }
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            if (type.GetString() == "instadesktop:notification") { LoggingService.Write(LogEvent.NotificationRejected); return; }
            if (type.GetString() == "instadesktop:unread")
            {
                // null: the Messages link is not on screen; keep the last count.
                if (sender == Core && root.TryGetProperty("count", out var unread) &&
                    unread.TryGetInt32(out int unreadCount) && unreadCount is >= 0 and <= 9999)
                    SetUnreadCount(unreadCount);
                return;
            }
            if (type.GetString() == "instadesktop:notification-diagnostic")
            {
                if (!_settings.Current.AppNotifications || !NotificationPolicy.IsInstagramOrigin(e.Source)) return;
                if (root.TryGetProperty("event", out var evt) && evt.ValueKind == JsonValueKind.String &&
                    root.TryGetProperty("count", out var count) && count.TryGetInt32(out int number) && number >= 0 && number <= 10000)
                {
                    int kind = evt.GetString() switch { "baseline" => 1, "permission" => 2, "workers" => 3,
                        "workers-active" => 4, "workers-unavailable" => 5, "wrapper-unavailable" => 6,
                        "rows-unavailable" => 7, _ => 0 };
                    if (kind != 0) LoggingService.Write(kind == 1 ? LogEvent.NotificationBaseline : LogEvent.NotificationDiagnostic,
                        code: kind * 10000 + number);
                }
                return;
            }
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException) { }
    }

    private async void SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        PublishNavigationState();
        if (Core is { } sourceCore && IsSessionRoute(sourceCore.Source)) StopDirectMonitor();
        else _ = RefreshDirectMonitorAsync();
        if (e.IsNewDocument || Core is not { } core ||
            !NavigationPolicy.IsTrusted(core.Source)) return;
        try { await core.ExecuteScriptAsync("window.dispatchEvent(new Event('instadesktop:navigation'));"); }
        catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
    }

    private void WindowCloseRequested(object? sender, object e)
    {
        // Evidence: a call page closing itself in the main view hides the app.
        LoggingService.Write(LogEvent.WindowCloseRequested, code: PathCategory(Core?.Source));
        CloseRequested?.Invoke();
    }

    // 0 other, 1 /direct/, 2 path mentions "call", 3 site root. Never the path itself.
    internal static int PathCategory(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return 0;
        string path = uri.AbsolutePath;
        if (path.Contains("call", StringComparison.OrdinalIgnoreCase)) return 2;
        if (path.StartsWith("/direct/", StringComparison.Ordinal)) return 1;
        return path == "/" ? 3 : 0;
    }

    public void SetBackground(bool background)
    {
        _background = background;
        if (NeedsRecovery || !_memoryApiAvailable || Core is not { } core) return;
        // Low target may discard renderer resources. Keep normal memory while
        // notifications are enabled; never suspend the WebView for tray mode.
        // A call window shares the opener's renderer; keep it at Normal during calls.
        var target = background && _settings.Current.BackgroundLowMemory && !_settings.Current.AppNotifications && _callWindows.Count == 0 && _messagesWindow is null
            ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        if (_memoryTarget == target) return;
        try
        {
            core.MemoryUsageTargetLevel = target;
            _memoryTarget = target;
        }
        catch (Exception e) when (e is NotImplementedException or NotSupportedException or COMException)
        {
            // Older installed runtimes may not expose this interface. Continue normally.
            _memoryApiAvailable = false;
        }
        catch (InvalidOperationException) { }
    }

    public async Task ReloadAsync()
    {
        if (NeedsRecovery || Core is null)
        {
            ShowRequested?.Invoke();
            await InitializeAsync();
            return;
        }
        await _operation.WaitAsync();
        try
        {
            if (_disposed || Core is null) return;
            Core.Reload();
        }
        catch (Exception e)
        {
            LoggingService.Write(LogEvent.NavigationError, e);
            Fail(Loc.T("Web.ReloadFailed"));
        }
        finally { _operation.Release(); }
    }

    public async Task ApplySettingsAsync()
    {
        // Only a changed switch overrides what was chosen in a call window's menu.
        if (_callWindowsOnTop != _settings.Current.CallWindowsOnTop)
        {
            _callWindowsOnTop = _settings.Current.CallWindowsOnTop;
            foreach (var call in _callWindows) call.SetOnTop(_callWindowsOnTop);
        }
        UpdateMediaSwitches();
        _notifications.DirectInboxMonitoring = MonitorAllowed;
        if (!_notifications.DirectInboxMonitoring) { _directMonitorCheck.Stop(); StopDirectMonitor(); }
        else _directMonitorCheck.Start();
        if (Core is not { } core) return;
        await SyncMediaPermissionsAsync(core, startup: false);
        core.Settings.AreDevToolsEnabled = _settings.Current.DeveloperTools;
        ApplyDownloadFolder(core);
        if (!_settings.Current.AppNotifications) _notifications.Reset(removeNotifications: true);
        await EnsureNotificationPermissionAsync(core);
        await ConfigureNotificationsAsync(core);
        _ = RefreshDirectMonitorAsync();
        SetBackground(_background);
    }

    public async Task<PermissionResetResult> ResetPermissionsAsync()
    {
        if (_disposed || NeedsRecovery || Core is not { } core) return PermissionResetResult.NotReady;
        await _mediaSync.WaitAsync();
        try
        {
            // An open question must not re-save a decision right after the reset.
            CancelPendingPermissions(null, null, MediaPermissionPolicy.Reason.Canceled);
            var permissions = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            int count = 0;
            foreach (var permission in permissions)
                if (NavigationPolicy.IsTrusted(permission.PermissionOrigin))
                {
                    await core.Profile.SetPermissionStateAsync(permission.PermissionKind, permission.PermissionOrigin,
                        CoreWebView2PermissionState.Default);
                    count++;
                }
            LoggingService.Write(LogEvent.MediaPermissionReset, code: Math.Min(count, 999));
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.MediaPermissionReset, error);
            return error is NotImplementedException or NotSupportedException ||
                (error is COMException com && com.HResult == unchecked((int)0x80004002))
                ? PermissionResetResult.Unsupported : PermissionResetResult.Failed;
        }
        finally { _mediaSync.Release(); }
        // Notification permission follows the desktop setting. Leaving it at
        // Default here makes Notification.permission stop being granted until
        // Instagram happens to ask again (or the app is restarted).
        await EnsureNotificationPermissionAsync(core);
        // Media stays at Default: asked again while on, denied per request while off.
        await SyncMediaPermissionsAsync(core, startup: false);
        return PermissionResetResult.Reset;
    }

    // Detects switch transitions made through Settings. Synchronous, so a pending
    // question can never be answered against an outdated switch value.
    private void UpdateMediaSwitches()
    {
        foreach (var kind in MediaPermissionPolicy.Kinds)
        {
            bool enabled = MediaPermissionPolicy.Enabled(_settings.Current, kind);
            if (_appliedMedia[kind] == enabled) continue;
            _appliedMedia[kind] = enabled;
            _mediaGeneration[kind]++;
            LoggingService.Write(LogEvent.MediaPermissionSync, code: (int)kind * 10 + (enabled ? 1 : 0));
            if (enabled) _clearDeniedMedia.Add(kind); // explicit re-enable: ask again
            else
            {
                _clearDeniedMedia.Remove(kind);
                CancelPendingPermissions(null, kind, MediaPermissionPolicy.Reason.Stale);
            }
        }
    }

    private async Task SyncMediaPermissionsAsync(CoreWebView2 core, bool startup)
    {
        await _mediaSync.WaitAsync();
        try
        {
            // Builds up to 1.1.0 saved the app's own "switch off" denial, which no
            // later event could undo, and it cannot be told apart from a user's
            // No. Recover conservatively, once: clear a saved Deny so Instagram
            // asks again. This migration never grants anything.
            bool migrate = startup && _settings.Current.MediaPermissionRevision < 1;
            foreach (var kind in MediaPermissionPolicy.Kinds)
            {
                if (startup) _appliedMedia[kind] = MediaPermissionPolicy.Enabled(_settings.Current, kind);
                bool clear = migrate || _clearDeniedMedia.Contains(kind);
                await MediaPermissionPolicy.SyncAsync(core.Profile, kind,
                    () => !_disposed && MediaPermissionPolicy.Enabled(_settings.Current, kind), () => clear);
                if (clear) _clearDeniedMedia.Remove(kind);
            }
            if (migrate && !_disposed)
            {
                await _settings.UpdateAsync(s => s.MediaPermissionRevision = 1);
                LoggingService.Write(LogEvent.MediaPermissionMigrated, code: 1);
            }
        }
        catch (Exception error) { LoggingService.Write(LogEvent.MediaPermissionSync, error); }
        finally { _mediaSync.Release(); }
    }

    // Settings > Clear cache: cached files and service workers only. Cookies,
    // site storage and permissions stay, so the user remains signed in.
    // null = Instagram is not loaded yet.
    public async Task<bool?> ClearCacheAsync()
    {
        if (_disposed || NeedsRecovery || Core is not { } core) return null;
        try
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.CacheStorage | CoreWebView2BrowsingDataKinds.ServiceWorkers);
            LoggingService.Write(LogEvent.CacheCleared);
            core.Reload();
            _messagesWindow?.View.CoreWebView2?.Reload();
            return true;
        }
        catch (Exception error) when (error is COMException or NotImplementedException or InvalidOperationException)
        {
            LoggingService.Write(LogEvent.CacheCleared, error);
            return false;
        }
    }

    // Combined Instagram site state for Settings; null when the profile is unavailable.
    internal async Task<CoreWebView2PermissionState?> GetMediaSiteStateAsync(CoreWebView2PermissionKind kind)
    {
        if (_disposed || NeedsRecovery || Core is not { } core) return null;
        try { return await MediaPermissionPolicy.SiteStateAsync(core.Profile, kind); }
        catch (Exception error) { LoggingService.Write(LogEvent.MediaPermissionSync, error); return null; }
    }

    private void Fail(string message, bool retry = true)
    {
        NeedsRecovery = true;
        FullscreenChanged?.Invoke(false);
        LastFailure = message;
        if (View is not null) View.Visibility = Visibility.Hidden;
        _canAutoRecover = retry && !_diagnosticInitialization;
        if (_canAutoRecover) ScheduleRecovery(RecoveryDelays[Math.Min(_recoveryAttempt, RecoveryDelays.Length - 1)]);
        StatusChanged?.Invoke(_canAutoRecover ? message + " " + Loc.T("Web.Retrying") : message, true);
    }

    // A failed load or crashed renderer recovers by itself (backoff, network
    // return, resume from sleep). Without this, a tray-only session started
    // before the network was ready never loads and never notifies.
    private static readonly TimeSpan[] RecoveryDelays =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)
    };

    private void ScheduleRecovery(TimeSpan delay)
    {
        if (_disposed || !NeedsRecovery || !_canAutoRecover) return;
        _recoveryTimer.Stop();
        _recoveryTimer.Interval = delay;
        _recoveryTimer.Start();
    }

    private async Task RecoverAsync()
    {
        _recoveryTimer.Stop();
        if (_disposed || !NeedsRecovery || !_canAutoRecover) return;
        // A manual Retry or another rebuild is already running; check again later.
        if (_operation.CurrentCount == 0) { ScheduleRecovery(RecoveryDelays[0]); return; }
        _recoveryAttempt++;
        LoggingService.Write(LogEvent.AutoRecoveryAttempt, code: _recoveryAttempt);
        await InitializeAsync();
    }

    private void NetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _owner.Dispatcher.BeginInvoke(new Action(OnNetworkAvailable));
    }

    // Also the diagnostic entry point: Windows reporting a network again.
    internal void OnNetworkAvailable() => ScheduleRecovery(TimeSpan.FromSeconds(3));

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _owner.Dispatcher.BeginInvoke(new Action(() => ScheduleRecovery(TimeSpan.FromSeconds(5))));
    }

    // Zoom: Ctrl+wheel (WebView2) and the window shortcuts share one factor,
    // saved shortly after it stops changing.
    private static readonly double[] ZoomLevels = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };
    public double ZoomFactor => View?.ZoomFactor ?? _settings.Current.ZoomFactor;

    public void StepZoom(int direction)
    {
        if (View is not { } view || NeedsRecovery) return;
        double current = view.ZoomFactor;
        view.ZoomFactor = direction > 0
            ? ZoomLevels.FirstOrDefault(level => level > current + 0.001, ZoomLevels[^1])
            : ZoomLevels.LastOrDefault(level => level < current - 0.001, ZoomLevels[0]);
        ZoomUpdated();
    }

    public void ResetZoom()
    {
        if (View is not { } view || NeedsRecovery) return;
        view.ZoomFactor = 1;
        ZoomUpdated();
    }

    // Ctrl+wheel arrives here; shortcuts also report directly so the indicator
    // never depends on the event timing.
    private void ViewZoomFactorChanged(object? sender, EventArgs e) => ZoomUpdated();

    private void ZoomUpdated()
    {
        if (View is not { } view) return;
        ZoomChanged?.Invoke(view.ZoomFactor);
        _zoomSave.Stop();
        _zoomSave.Start();
    }

    private async Task SaveZoomAsync()
    {
        _zoomSave.Stop();
        if (View is not { } view) return;
        double factor = Math.Round(view.ZoomFactor, 3);
        if (Math.Abs(factor - _settings.Current.ZoomFactor) < 0.001) return;
        try { await _settings.UpdateAsync(s => s.ZoomFactor = factor); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    private void DisposeView()
    {
        _directMonitorCheck.Stop();
        StopDirectMonitor();
        _notifications.Reset(removeNotifications: false);
        _notificationScriptId = null;
        // Complete deferrals while their WebView is still alive. Call windows
        // are independent views and keep running across a main-page rebuild.
        if (_primaryCore is { } primary)
        {
            CancelPendingPermissions(primary, null, MediaPermissionPolicy.Reason.Canceled);
            _documentEpochs.Remove(primary);
        }
        _primaryCore = null;
        if (View is null) return;
        View.Dispose();
        _host.Children.Remove(View);
        View = null;
    }

    private static bool IsSessionRoute(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.AbsolutePath.StartsWith("/accounts/", StringComparison.Ordinal) ||
         uri.AbsolutePath.StartsWith("/challenge", StringComparison.Ordinal));

    internal async Task RefreshDirectMonitorAsync()
    {
        if (_disposed || !MonitorAllowed || _checkingDirectMonitor ||
            NeedsRecovery || Core is not { } primary || !NotificationPolicy.IsInstagramOrigin(primary.Source) || IsSessionRoute(primary.Source)) return;
        if (_directMonitor?.IsRunning == true) return;
        if (DateTimeOffset.UtcNow < _nextDirectMonitorAttempt) return;
        _checkingDirectMonitor = true;
        try
        {
            // Observe signed-in UI readiness only. Never inspect cookies/tokens.
            string usable = await primary.ExecuteScriptAsync("Boolean(document.querySelector('a[href=\"/direct/inbox/\"],a[href=\"/direct/inbox\"]') && !document.querySelector('input[type=\"password\"]'))")
                .WaitAsync(TimeSpan.FromSeconds(2));
            if (_disposed || !MonitorAllowed || Core != primary || NeedsRecovery || IsSessionRoute(primary.Source) || usable != "true") return;
            // Exactly one monitor per primary: the previous one (stopped by a
            // route block, crash or reset) is closed with its handlers first.
            _directMonitor?.Dispose();
            _nextDirectMonitorAttempt = DateTimeOffset.UtcNow.AddMinutes(2);
            var monitor = new DirectInboxMonitor(_owner.Dispatcher, _notifications, () => !_disposed && _settings.Current.AppNotifications);
            _directMonitor = monitor;
            await monitor.StartAsync(primary, new WindowInteropHelper(_owner).Handle, DiagnosticMonitorConfigure);
        }
        catch (Exception error) { LoggingService.Write(LogEvent.DirectMonitorInitializationFailed, error); }
        finally { _checkingDirectMonitor = false; }
    }

    // Diagnostics: skip the restart backoff so a test can exercise a restart.
    internal void ExpireDirectMonitorBackoff() => _nextDirectMonitorAttempt = DateTimeOffset.MinValue;

    private void StopDirectMonitor()
    {
        _directMonitor?.Dispose();
        _directMonitor = null;
        _nextDirectMonitorAttempt = DateTimeOffset.MinValue;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            if (_zoomSave.IsEnabled) _ = SaveZoomAsync();
        }
        _disposed = true;
        _recoveryTimer.Stop();
        foreach (var window in _callWindows.ToArray()) window.Close();
        _messagesIdle?.Stop();
        _messagesWindow?.CloseForGood();
        ThemeService.Changed -= ApplyTheme;
        DisposeView();
    }

    public void NavigateNotificationThread(string? url)
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed || NotificationPolicy.DirectUrl(url) is not { } safe) return;
        if (Core is not { } core || NeedsRecovery || _operation.CurrentCount == 0)
        {
            _pendingNotificationThread = safe;
            if (NeedsRecovery) _ = InitializeAsync();
            return;
        }
        core.Navigate(safe);
        LoggingService.Write(LogEvent.NotificationNavigated);
    }

    private void PublishNavigationState()
    {
        if (NeedsRecovery || Core is not { } core || !NavigationPolicy.IsTrusted(core.Source)) return;
        CurrentSection = InstagramRoutes.FromPath(new Uri(core.Source).AbsolutePath);
    }

    public async Task NavigateSectionAsync(NavigationSection section)
    {
        if (InstagramRoutes.PathFor(section) is not { } path) return;
        if (Core is null || NeedsRecovery) await InitializeAsync();
        if (Core is not { } core || NeedsRecovery) return;
        core.Navigate("https://www.instagram.com" + path);
    }
}
