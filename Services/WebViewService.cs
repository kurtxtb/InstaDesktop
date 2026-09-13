using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private DateTime _notificationReadyAt = DateTime.UtcNow;
    private CoreWebView2MemoryUsageTargetLevel? _memoryTarget;
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
    public event Action<string, string>? DesktopNotification;
    public NavigationSection CurrentSection { get; private set; } = NavigationSection.Home;

    public WebViewService(Grid host, Window owner, SettingsService settings)
    {
        _host = host;
        _owner = owner;
        _settings = settings;
    }

    public async Task InitializeAsync()
    {
        await _operation.WaitAsync();
        try
        {
            if (_disposed) return;
            DisposeView();
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
            await ConfigureCoreAsync(View.CoreWebView2);
            await EnsureNotificationPermissionAsync(View.CoreWebView2);
            await UpdateInjectionAsync();
            SetBackground(_background);
            View.CoreWebView2.Navigate(NavigationPolicy.Home);
            _notificationReadyAt = DateTime.UtcNow.AddSeconds(4);
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

    private static async Task EnsureNotificationPermissionAsync(CoreWebView2 core)
    {
        // Instagram uses the Web Notifications API for message alerts. A
        // previous denial is persisted in the WebView2 profile, so explicitly
        // restore the default permission on every startup.
        try
        {
            await core.Profile.SetPermissionStateAsync(
                CoreWebView2PermissionKind.Notifications,
                NavigationPolicy.Home,
                CoreWebView2PermissionState.Allow);
        }
        catch (Exception error) when (error is COMException or NotImplementedException)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
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
        core.NotificationReceived += NotificationReceived;
        await core.AddScriptToExecuteOnDocumentCreatedAsync("""
            (() => {
              try {
                const NativeNotification = window.Notification;
                if (!NativeNotification || NativeNotification.__instaDesktopWrapped) return;
                function InstaDesktopNotification(title, options) {
                  try { window.chrome?.webview?.postMessage(JSON.stringify({type:'instadesktop:notification', title:String(title||'Instagram'), body:String(options?.body||'')})); } catch {}
                  return new NativeNotification(title, options);
                }
                InstaDesktopNotification.prototype = NativeNotification.prototype;
                for (const key of ['permission','requestPermission']) {
                  try { Object.defineProperty(InstaDesktopNotification, key, {get:()=>NativeNotification[key]}); } catch {}
                }
                InstaDesktopNotification.__instaDesktopWrapped = true;
                try { Object.defineProperty(window, 'Notification', {value: InstaDesktopNotification, configurable: true}); }
                catch { window.Notification = InstaDesktopNotification; }
                try { window.chrome?.webview?.postMessage('instadesktop:notification-bridge-ready'); } catch {}
                const seen = new Set();
                const scan = node => {
                  try {
                    const el = node?.nodeType === 1 ? node : node?.parentElement;
                    if (!el) return;
                    const live = el.matches('[role="alert"],[aria-live="polite"],[aria-live="assertive"]') ? el : el.querySelector('[role="alert"],[aria-live="polite"],[aria-live="assertive"]');
                    if (!live || seen.has(live)) return;
                    const body = (live.innerText || live.textContent || '').replace(/\s+/g,' ').trim();
                    if (!body || body.length < 3 || body.length > 500) return;
                    seen.add(live);
                    const direct = location.pathname.toLowerCase().startsWith('/direct');
                    window.chrome?.webview?.postMessage(JSON.stringify({type:'instadesktop:notification', title: direct ? 'Instagram Direct' : 'Instagram', body}));
                  } catch {}
                };
                new MutationObserver(ms => ms.forEach(m => m.addedNodes.forEach(scan))).observe(document.documentElement, {subtree:true, childList:true});
                let lastUnread = -1;
                const checkDirectUnread = () => {
                  try {
                    if (!location.pathname.toLowerCase().startsWith('/direct')) { lastUnread = -1; return; }
                    const match = (document.title || '').match(/\(\s*(\d+)\s*\)/);
                    const count = match ? Number(match[1]) : 0;
                    if (lastUnread >= 0 && count > lastUnread && performance.now() > 5000)
                      window.chrome?.webview?.postMessage(JSON.stringify({type:'instadesktop:notification', title:'Instagram Direct', body:'You have a new direct message.'}));
                    lastUnread = count;
                  } catch {}
                };
                setInterval(checkDirectUnread, 1500);
                let lastBadge = null;
                const checkUnreadBadges = () => {
                  try {
                    const nodes = [...document.querySelectorAll('[aria-label],[title],[data-testid]')];
                    const hits = nodes.filter(el => /unread|new message|direct message|未讀|新訊息/i.test((el.getAttribute('aria-label')||'')+' '+(el.getAttribute('title')||'')+' '+(el.getAttribute('data-testid')||'')));
                    const signature = hits.map(el => (el.getAttribute('aria-label')||el.getAttribute('title')||el.getAttribute('data-testid')||'').slice(0,120)).sort().join('|');
                    if (lastBadge === null) { lastBadge = signature; return; }
                    if (signature !== lastBadge && hits.length > 0 && performance.now() > 5000) {
                      const direct = location.pathname.toLowerCase().startsWith('/direct') || /direct/i.test(signature);
                      window.chrome?.webview?.postMessage(JSON.stringify({type:'instadesktop:notification', title: direct ? 'Instagram Direct' : 'Instagram', body: direct ? 'You have a new direct message.' : 'You have new Instagram activity.'}));
                    }
                    lastBadge = signature;
                  } catch {}
                };
                setInterval(checkUnreadBadges, 2000);
              } catch {}
            })();
            """);
    }

    private void NotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        LoggingService.Write(LogEvent.NotificationReceived);
        if (DateTime.UtcNow < _notificationReadyAt) return;
        // WebView2's default notification surface is not consistently exposed
        // as a Windows desktop toast for unpackaged WPF apps. Handle it here
        // and route it through the app's tray icon instead.
        e.Handled = true;
        if (!_settings.Current.AppNotifications) return;
        try
        {
            DesktopNotification?.Invoke(
                string.IsNullOrWhiteSpace(e.Notification.Title) ? "Instagram" : e.Notification.Title,
                string.IsNullOrWhiteSpace(e.Notification.Body) ? "You have a new notification." : e.Notification.Body);
            e.Notification.ReportShown();
        }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
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
        { e.State = CoreWebView2PermissionState.Allow; e.SavesInProfile = true; return; }
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
            Fail("Instagram renderer crashed.");
        // WebView2 automatically recovers ancillary GPU/utility processes.
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!NavigationPolicy.IsTrusted(e.Source)) return;
        try
        {
            string json = e.WebMessageAsJson;
            if (!_settings.Current.UiCustomization && !json.Contains("instadesktop:notification", StringComparison.Ordinal)) return;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                var text = doc.RootElement.GetString();
                if (text?.StartsWith("{\"type\":\"instadesktop:notification\"", StringComparison.Ordinal) == true)
                {
                    LoggingService.Write(LogEvent.NotificationReceived);
                    using var notification = JsonDocument.Parse(text);
                    var notificationRoot = notification.RootElement;
                    DesktopNotification?.Invoke(notificationRoot.GetProperty("title").GetString() ?? "Instagram", notificationRoot.GetProperty("body").GetString() ?? "You have a new notification.");
                    return;
                }
            }
            if (json.Length > 4096) return;
            if (json == "\"instadesktop:notification-bridge-ready\"") { LoggingService.Write(LogEvent.NotificationReceived, code: 1); return; }
            if (json == "\"instadesktop:injection-error\"") { ReportInjectionError(); return; }
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
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
        if (e.IsNewDocument || !_settings.Current.UiCustomization || Core is not { } core ||
            !NavigationPolicy.IsTrusted(core.Source)) return;
        try { await core.ExecuteScriptAsync("window.dispatchEvent(new Event('instadesktop:navigation'));"); }
        catch (Exception error) { LoggingService.Write(LogEvent.InjectionError, error); }
    }

    private void WindowCloseRequested(object? sender, object e) => CloseRequested?.Invoke();

    public void SetBackground(bool background)
    {
        _background = background;
        if (NeedsRecovery || !_memoryApiAvailable || Core is not { } core) return;
        var target = background && _settings.Current.BackgroundLowMemory
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
        if (Core is not { } core) return;
        core.Settings.AreDevToolsEnabled = _settings.Current.DeveloperTools;
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
        if (View is null) return;
        View.Dispose();
        _host.Children.Remove(View);
        View = null;
    }

    public void Dispose()
    {
        _disposed = true;
        DisposeView();
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
