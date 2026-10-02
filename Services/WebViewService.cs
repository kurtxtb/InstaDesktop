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
using InstaDesktop.Models;

namespace InstaDesktop.Services;

public enum PermissionResetResult { Reset, NotReady, Unsupported, Failed }

public sealed class WebViewService : IDisposable
{
    private readonly Grid _host;
    private readonly Window _owner;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private InjectionService _injection = new();
    private bool _disposed;
    private bool _memoryApiAvailable = true;
    private bool _background;
    private bool _injectionErrorReported;
    private string? _notificationScriptId;
    private string? _pendingNotificationThread;
    private readonly NotificationService _notifications;
    private DirectInboxMonitor? _directMonitor;
    private readonly DispatcherTimer _directMonitorCheck;
    private bool _checkingDirectMonitor, _diagnosticInitialization;
    private DateTimeOffset _nextDirectMonitorAttempt;
    private CoreWebView2MemoryUsageTargetLevel? _memoryTarget;
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
    internal static TimeSpan BlockedNoticeInterval { get; set; } = TimeSpan.FromSeconds(60);
    // Raised (throttled) when a call is blocked by a desktop switch that is off.
    public event Action<CoreWebView2PermissionKind>? MediaBlocked;
    private bool MonitorAllowed => _settings.Current.AppNotifications &&
        (!_diagnosticInitialization || DiagnosticMonitorConfigure is not null);
    public WebView2? View { get; private set; }
    public CoreWebView2? Core => View?.CoreWebView2;
    public bool NeedsRecovery { get; private set; }
    public bool MemoryApiAvailable => _memoryApiAvailable;
    public string? LastFailure { get; private set; }
    public event Action<string, bool>? StatusChanged;
    public event Action? Ready;
    public event Action? ShowRequested;
    public event Action? CloseRequested;
    public event Action? InjectionFailed;
    public event Action<NavigationSection>? RouteChanged;
    public event Action<bool, bool>? HistoryChanged;
    public event Action<bool>? FullscreenChanged;
    public event Action<string>? UserNotice;
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
            DisposeView();
            _diagnosticInitialization = diagnosticConfigure is not null;
            NeedsRecovery = false;
            LastFailure = null;
            _memoryApiAvailable = true;
            _memoryTarget = null;
            _injection = new();
            StatusChanged?.Invoke("Loading Instagram...", false);
            Directory.CreateDirectory(AppPaths.UserData);
            string arguments = _settings.Current.HardwareAcceleration ? "" : "--disable-gpu";
            if (_diagnosticInitialization && DiagnosticBrowserArguments is { } diagnosticArguments)
                arguments = (arguments + " " + diagnosticArguments).Trim();
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = arguments };
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.UserData, options: options)
                .WaitAsync(TimeSpan.FromSeconds(30));
            if (_disposed) return;
            View = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(16, 16, 18), ZoomFactor = 1.0 };
            _host.Children.Add(View);
            await View.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
            if (_disposed) return;
            diagnosticConfigure?.Invoke(View.CoreWebView2);
            await ConfigureCoreAsync(View.CoreWebView2);
            // Before the first navigation: the page must never see a stale grant.
            await SyncMediaPermissionsAsync(View.CoreWebView2, startup: true);
            if (_disposed) return;
            _notifications.DirectInboxMonitoring = MonitorAllowed;
            if (_notifications.DirectInboxMonitoring) _directMonitorCheck.Start();
            await EnsureNotificationPermissionAsync(View.CoreWebView2);
            await UpdateInjectionAsync();
            SetBackground(_background);
            View.CoreWebView2.Navigate(_pendingNotificationThread ?? NavigationPolicy.Home);
            _pendingNotificationThread = null;
        }
        catch (Exception e)
        {
            if (_disposed) return;
            LoggingService.Write(LogEvent.WebViewInitializationError, e);
            bool runtimeMissing = e is WebView2RuntimeNotFoundException;
            Fail(runtimeMissing ? "Microsoft Edge WebView2 Runtime is required." :
                "Unable to initialize Instagram. Retry or check the app log.");
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
        s.IsZoomControlEnabled = false;
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
        core.ContainsFullScreenElementChanged += (_, _) => FullscreenChanged?.Invoke(core.ContainsFullScreenElement);
        try { core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark; }
        catch (Exception e) when (e is NotImplementedException or COMException) { }
        try { core.NotificationReceived += NotificationReceived; }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationInitializationFailed, error); }
        try { await EmojiFontService.ConfigureAsync(core); }
        catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
        await ConfigureNotificationsAsync(core);
    }

    private async Task ConfigureNotificationsAsync(CoreWebView2 core)
    {
        try
        {
            // Use the embedded bridge so upgrades cannot leave an obsolete copy
            // behind when the installer preserves user-editable customization.
            string script = "window.__InstaDesktopNotificationsEnabled = " +
                (_settings.Current.AppNotifications ? "true;\n" : "false;\n") +
                await ReadNotificationAssetAsync("direct-inbox-dom.js") + "\n" + await ReadNotificationAssetAsync("notifications.js");
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
    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsSessionRoute(e.Uri)) StopDirectMonitor();
        if (NavigationPolicy.IsTrusted(e.Uri))
        {
            _injectionErrorReported = false;
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

    private async void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed) return;
        if (!e.IsSuccess)
        {
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            LoggingService.Write(LogEvent.NavigationError, code: (int)e.WebErrorStatus);
            Fail("Instagram could not load. Check your connection and try again.");
            return;
        }
        NeedsRecovery = false;
        LastFailure = null;
        if (View is not null) View.Visibility = Visibility.Visible;
        Ready?.Invoke();
        _ = RefreshDirectMonitorAsync();
        PublishNavigationState();
        // Keep the original Instagram UI while preventing WebView scrollbars
        // from painting a white gutter over the dark page.
        if (Core is { } viewportCore && NavigationPolicy.IsTrusted(viewportCore.Source))
        {
            try
            {
                await viewportCore.ExecuteScriptAsync("(()=>{let s=document.getElementById('instadesktop-viewport-style');if(!s){s=document.createElement('style');s.id='instadesktop-viewport-style';s.textContent='html,body{background:#000!important;overflow-x:clip!important;max-width:100vw!important;}html{scrollbar-width:none!important;}html::-webkit-scrollbar,body::-webkit-scrollbar{display:none!important;width:0!important;height:0!important;}';document.head?.appendChild(s);}else{s.textContent='html,body{background:#000!important;overflow-x:clip!important;max-width:100vw!important;}html{scrollbar-width:none!important;}html::-webkit-scrollbar,body::-webkit-scrollbar{display:none!important;width:0!important;height:0!important;}'}})();");
            }
            catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
        }
        if (!_settings.Current.UiCustomization && Core is { } cleanCore && NavigationPolicy.IsTrusted(cleanCore.Source))
        {
            try { await cleanCore.ExecuteScriptAsync("document.getElementById('instadesktop-custom-style')?.remove(); for (const a of [...document.documentElement.attributes]) if (a.name.startsWith('data-instadesktop')) document.documentElement.removeAttribute(a.name);"); }
            catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
        }
        if (_settings.Current.UiCustomization && Core is { } core && NavigationPolicy.IsTrusted(core.Source))
        {
            try
            {
                string result = await core.ExecuteScriptAsync("Boolean(window.__InstaDesktopCustomization?.version === 1)");
                if (result != "true") ReportInjectionError();
            }
            catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
        }
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
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
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
                window = new CallWindow(width, height, _owner);
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
                if (!_disposed) UserNotice?.Invoke("The Instagram call window could not open.\nTry again, or restart InstaDesktop.");
            }
            finally
            {
                try { deferral.Complete(); }
                catch (Exception error) when (error is COMException or InvalidOperationException) { LoggingService.Write(LogEvent.CallWindowFailed, error); }
            }
        }));
    }

    private void ConfigureCallCore(CoreWebView2 core, CallWindow window)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled = _settings.Current.DeveloperTools;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
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
        core.DocumentTitleChanged += (_, _) =>
            window.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Instagram call" : core.DocumentTitle;
        core.ContainsFullScreenElementChanged += (_, _) => window.SetFullscreen(core.ContainsFullScreenElement);
        // The call page ending itself closes only its own window.
        core.WindowCloseRequested += (_, _) =>
        {
            LoggingService.Write(LogEvent.WindowCloseRequested, code: 10 + PathCategory(core.Source));
            _owner.Dispatcher.BeginInvoke(new Action(window.Close));
        };
        core.ProcessFailed += (_, e) =>
        {
            LoggingService.Write(LogEvent.WebViewProcessError, code: 2000 + (int)e.ProcessFailedKind);
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                CancelPendingPermissions(core, null, MediaPermissionPolicy.Reason.Canceled);
                _owner.Dispatcher.BeginInvoke(new Action(window.Close));
            }
        };
        window.Closed += (_, _) =>
        {
            CancelPendingPermissions(core, null, MediaPermissionPolicy.Reason.Canceled);
            _documentEpochs.Remove(core);
            _callWindows.Remove(window);
            try { window.View.Dispose(); }
            catch (Exception error) { LoggingService.Write(LogEvent.CallWindowFailed, error); }
            LoggingService.Write(LogEvent.CallWindowClosed, code: _callWindows.Count);
            SetBackground(_background);
        };
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
                    Window owner = _callWindows.FirstOrDefault(w => w.View.CoreWebView2 == first.Core) ?? _owner;
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
        string names = string.Join(" and ", kinds.Select(kind => kind switch
        {
            CoreWebView2PermissionKind.Microphone => "microphone",
            CoreWebView2PermissionKind.Camera => "camera",
            CoreWebView2PermissionKind.Notifications => "notifications",
            _ => kind.ToString()
        }));
        string detail = kinds.Any(MediaPermissionPolicy.IsMediaKind)
            ? "\n\nInstaDesktop remembers your answer. To be asked again, turn the setting off and on in Settings, or use Reset website permissions."
            : "\n\nYou can reset website permissions in Settings.";
        return MessageBox.Show(owner, $"Allow {host} to use your {names}?{detail}",
            "Instagram permission", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_disposed) { e.Cancel = true; return; }
                ShowRequested?.Invoke();
                var dialog = new SaveFileDialog
                {
                    Title = "Save Instagram download",
                    FileName = Path.GetFileName(e.ResultFilePath),
                    Filter = "All files (*.*)|*.*",
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
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            if (sender is CoreWebView2 failed) CancelPendingPermissions(failed, null, MediaPermissionPolicy.Reason.Canceled);
            // The page that owned these native notifications is gone.
            if (sender is not null) _notifications.ReleaseNative(sender);
            Fail("Instagram renderer crashed.");
        }
        // WebView2 automatically recovers ancillary GPU/utility processes.
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
            if (root.ValueKind == JsonValueKind.String)
            {
                if (_settings.Current.UiCustomization && root.GetString() == "instadesktop:injection-error") ReportInjectionError();
                return;
            }
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            if (type.GetString() == "instadesktop:notification") { LoggingService.Write(LogEvent.NotificationRejected); return; }
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
            if (!_settings.Current.UiCustomization || json.Length > 4096) return;
            if (type.GetString() is "routeChanged" or "pageLoaded")
            {
                // Page messages only request a refresh. Core.Source remains authoritative.
                PublishNavigationState();
            }
            else if (type.GetString() == "actionResult" && root.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && Enum.TryParse<NavigationSection>(action.GetString(), true, out var section) &&
                section is NavigationSection.Search or NavigationSection.Notifications or NavigationSection.Profile or NavigationSection.Create)
            {
                if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                    RouteChanged?.Invoke(section);
                if (root.TryGetProperty("fallback", out var fallback) && fallback.ValueKind == JsonValueKind.True)
                    UserNotice?.Invoke("Instagram's original navigation is visible for this panel. Use the toolbar menu to return to the desktop layout.");
            }
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException) { }
    }

    private void ReportInjectionError()
    {
        if (_injectionErrorReported) return;
        _injectionErrorReported = true;
        LoggingService.Write(LogEvent.InjectionError);
        InjectionFailed?.Invoke();
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
        var target = background && _settings.Current.BackgroundLowMemory && !_settings.Current.AppNotifications && _callWindows.Count == 0
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
            await UpdateInjectionAsync();
            Core.Reload();
        }
        catch (Exception e)
        {
            LoggingService.Write(LogEvent.NavigationError, e);
            Fail("Instagram could not reload. Try again.");
        }
        finally { _operation.Release(); }
    }

    private async Task UpdateInjectionAsync()
    {
        if (Core is null) return;
        if (!_settings.Current.UiCustomization)
        {
            // Remove customization injected by an earlier build/profile so the
            // original Instagram layout cannot retain a blank navigation gutter.
            try { await Core.ExecuteScriptAsync("document.getElementById('instadesktop-custom-style')?.remove(); for (const a of [...document.documentElement.attributes]) if (a.name.startsWith('data-instadesktop')) document.documentElement.removeAttribute(a.name);"); }
            catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
            return;
        }
        try { await _injection.ConfigureAsync(Core, _settings.Current.UiCustomization, _settings.Current.CompactInstagramLayout); }
        catch (Exception e)
        {
            LoggingService.Write(LogEvent.InjectionError, e);
            InjectionFailed?.Invoke();
        }
    }

    public async Task ApplySettingsAsync(bool customizationChanged)
    {
        UpdateMediaSwitches();
        _notifications.DirectInboxMonitoring = MonitorAllowed;
        if (!_notifications.DirectInboxMonitoring) { _directMonitorCheck.Stop(); StopDirectMonitor(); }
        else _directMonitorCheck.Start();
        if (Core is not { } core) return;
        await SyncMediaPermissionsAsync(core, startup: false);
        core.Settings.AreDevToolsEnabled = _settings.Current.DeveloperTools;
        if (!_settings.Current.AppNotifications) _notifications.Reset(removeNotifications: true);
        await EnsureNotificationPermissionAsync(core);
        await ConfigureNotificationsAsync(core);
        _ = RefreshDirectMonitorAsync();
        SetBackground(_background);
        if (customizationChanged) await ReloadCustomizationAsync();
        else if (_settings.Current.UiCustomization)
        {
            await _injection.ConfigureAsync(core, true, _settings.Current.CompactInstagramLayout);
            if (NavigationPolicy.IsTrusted(core.Source))
                await core.ExecuteScriptAsync("window.__InstaDesktopCustomization?.setCompact(" + (_settings.Current.CompactInstagramLayout ? "true" : "false") + ");");
        }
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
                var next = _settings.Current.Copy();
                next.MediaPermissionRevision = 1;
                await _settings.SaveAsync(next);
                LoggingService.Write(LogEvent.MediaPermissionMigrated, code: 1);
            }
        }
        catch (Exception error) { LoggingService.Write(LogEvent.MediaPermissionSync, error); }
        finally { _mediaSync.Release(); }
    }

    // Combined Instagram site state for Settings; null when the profile is unavailable.
    internal async Task<CoreWebView2PermissionState?> GetMediaSiteStateAsync(CoreWebView2PermissionKind kind)
    {
        if (_disposed || NeedsRecovery || Core is not { } core) return null;
        try { return await MediaPermissionPolicy.SiteStateAsync(core.Profile, kind); }
        catch (Exception error) { LoggingService.Write(LogEvent.MediaPermissionSync, error); return null; }
    }

    private void Fail(string message)
    {
        NeedsRecovery = true;
        FullscreenChanged?.Invoke(false);
        HistoryChanged?.Invoke(false, false);
        LastFailure = message;
        if (View is not null) View.Visibility = Visibility.Hidden;
        StatusChanged?.Invoke(message, true);
    }

    private void DisposeView()
    {
        _directMonitorCheck.Stop();
        StopDirectMonitor();
        _notifications.Reset(removeNotifications: false);
        _notificationScriptId = null;
        // Complete deferrals while their WebView is still alive. Call windows
        // are independent views and keep running across a main-page rebuild.
        if (View?.CoreWebView2 is { } primary)
        {
            CancelPendingPermissions(primary, null, MediaPermissionPolicy.Reason.Canceled);
            _documentEpochs.Remove(primary);
        }
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
        _disposed = true;
        foreach (var window in _callWindows.ToArray()) window.Close();
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
        if (NeedsRecovery || Core is not { } core) return;
        HistoryChanged?.Invoke(core.CanGoBack, core.CanGoForward);
        if (!NavigationPolicy.IsTrusted(core.Source)) return;
        CurrentSection = InstagramRoutes.FromPath(new Uri(core.Source).AbsolutePath);
        RouteChanged?.Invoke(CurrentSection);
    }

    public async Task NavigateSectionAsync(NavigationSection section)
    {
        if (Core is null || NeedsRecovery) await InitializeAsync();
        if (Core is not { } core || NeedsRecovery) return;
        if (InstagramRoutes.PathFor(section) is { } path) { core.Navigate("https://www.instagram.com" + path); return; }
        if (!NavigationPolicy.IsTrusted(core.Source)) return;
        string action = JsonSerializer.Serialize(section.ToString().ToLowerInvariant());
        string result = await core.ExecuteScriptAsync("Boolean(window.__InstaDesktopCustomization?.invokeAction(" + action + "))");
        if (result != "true")
        {
            await RevealWebNavigationAsync(true);
            UserNotice?.Invoke($"{section} could not be opened automatically. Sign in if needed, then use Instagram's original navigation in the content area.");
            PublishNavigationState();
        }
    }

    public async Task ReloadCustomizationAsync()
    {
        await _operation.WaitAsync();
        try
        {
            if (_disposed || NeedsRecovery || Core is not { } core) return;
            await _injection.RefreshAsync(core, _settings.Current.UiCustomization, _settings.Current.CompactInstagramLayout);
        }
        catch (Exception e) { LoggingService.Write(LogEvent.InjectionError, e); InjectionFailed?.Invoke(); }
        finally { _operation.Release(); }
    }

    public async Task RevealWebNavigationAsync(bool? show = null)
    {
        if (Core is not { } core || NeedsRecovery || !NavigationPolicy.IsTrusted(core.Source)) return;
        string argument = show.HasValue ? (show.Value ? "true" : "false") : "undefined";
        await core.ExecuteScriptAsync("window.__InstaDesktopCustomization?.revealNavigation(" + argument + ");");
    }
}
