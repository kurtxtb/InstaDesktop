using System;
using System.Collections.Generic;
using System.IO;
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
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = _settings.Current.HardwareAcceleration ? "" : "--disable-gpu"
            };
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.UserData, options: options)
                .WaitAsync(TimeSpan.FromSeconds(30));
            if (_disposed) return;
            View = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(16, 16, 18), ZoomFactor = 1.0 };
            _host.Children.Add(View);
            await View.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
            if (_disposed) return;
            diagnosticConfigure?.Invoke(View.CoreWebView2);
            await ConfigureCoreAsync(View.CoreWebView2);
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
        e.Handled = true;
        string uri = e.Uri;
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (NavigationPolicy.IsTrusted(uri)) Core?.Navigate(uri);
            else if (NavigationPolicy.UpgradeInstagramHttp(uri) is { } https) Core?.Navigate(https);
            else ShellService.OpenWeb(uri);
        }));
    }

    private void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
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
        if (e.PermissionKind == CoreWebView2PermissionKind.Microphone && !_settings.Current.AllowMicrophone)
        { e.State = CoreWebView2PermissionState.Deny; return; }
        if (e.PermissionKind == CoreWebView2PermissionKind.Camera && !_settings.Current.AllowCamera)
        { e.State = CoreWebView2PermissionState.Deny; return; }
        // Defer modal UI until after the WebView callback returns (COM reentrancy).
        var deferral = e.GetDeferral();
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_disposed) { e.State = CoreWebView2PermissionState.Deny; return; }
                ShowRequested?.Invoke();
                string name = e.PermissionKind switch
                {
                    CoreWebView2PermissionKind.Microphone => "microphone",
                    CoreWebView2PermissionKind.Camera => "camera",
                    CoreWebView2PermissionKind.Notifications => "notifications",
                    _ => e.PermissionKind.ToString()
                };
                bool allow = MessageBox.Show(_owner,
                    $"Allow {new Uri(e.Uri).Host} to use {name}?\n\nYou can reset website permissions in Settings.",
                    "Instagram permission", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
                e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                try { e.SavesInProfile = true; }
                catch (NotImplementedException) { }
                catch (COMException) { }
            }
            catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
            finally { deferral.Complete(); }
        }));
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

    private void WindowCloseRequested(object? sender, object e) => CloseRequested?.Invoke();

    public void SetBackground(bool background)
    {
        _background = background;
        if (NeedsRecovery || !_memoryApiAvailable || Core is not { } core) return;
        // Low target may discard renderer resources. Keep normal memory while
        // notifications are enabled; never suspend the WebView for tray mode.
        var target = background && _settings.Current.BackgroundLowMemory && !_settings.Current.AppNotifications
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
        _notifications.DirectInboxMonitoring = MonitorAllowed;
        if (!_notifications.DirectInboxMonitoring) { _directMonitorCheck.Stop(); StopDirectMonitor(); }
        else _directMonitorCheck.Start();
        if (Core is not { } core) return;
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

    public async Task ResetPermissionsAsync()
    {
        if (Core is not { } core) return;
        var permissions = await core.Profile.GetNonDefaultPermissionSettingsAsync();
        foreach (var permission in permissions)
            if (NavigationPolicy.IsTrusted(permission.PermissionOrigin))
                await core.Profile.SetPermissionStateAsync(permission.PermissionKind, permission.PermissionOrigin,
                    CoreWebView2PermissionState.Default);
        // Notification permission follows the desktop setting. Leaving it at
        // Default here makes Notification.permission stop being granted until
        // Instagram happens to ask again (or the app is restarted).
        await EnsureNotificationPermissionAsync(core);
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
