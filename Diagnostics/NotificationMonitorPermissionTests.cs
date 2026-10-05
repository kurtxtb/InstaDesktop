using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using InstaDesktop.Models;
using InstaDesktop.Services;
using Microsoft.Web.WebView2.Core;
using Windows.UI.Notifications;

namespace InstaDesktop.Diagnostics;

// Regression coverage for the hidden Direct inbox permission path. None of the
// WebView checks grant Notifications from the test itself: grants must come from
// the app's own PermissionRequested handler or its settings synchronization.
internal static class NotificationMonitorPermissionTests
{
    private sealed class Presenter : IWindowsNotificationPresenter
    {
        public event Action<string, bool>? Dismissed { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public List<(InstagramNotification Value, string Token, bool Replace)> Shows { get; } = new();
        public bool Show(InstagramNotification value, string token, string? avatar, string? image, bool replace)
        { Shows.Add((value, token, replace)); return true; }
        public int Removed;
        public void Remove(string token) => Removed++;
        public int Popups => Shows.Count(x => !x.Replace);
    }

    private sealed class FakeNotifier : IToastNotifierAdapter
    {
        public NotificationSetting? Value = NotificationSetting.Enabled;
        public Exception? ShowError;
        public List<ToastNotification> Shown { get; } = new();
        public NotificationSetting Setting => Value ?? throw new InvalidOperationException("setting unavailable");
        public void Show(ToastNotification toast) { if (ShowError is not null) throw ShowError; Shown.Add(toast); }
    }

    private const string InboxFixture = """
        <!doctype html><html><body><a href="/direct/inbox/">Inbox</a>
        <a id="row" href="/direct/t/123/" data-unread="false" data-unread-count="0">
        <span data-conversation-name>Alice</span><span id="preview" data-message-preview>old</span></a>
        </body></html>
        """;
    private const string PrimaryFixture = """
        <!doctype html><html><body>
        <a id="inbox" href="/direct/inbox/">Messages<span id="badge">0</span></a>
        </body></html>
        """;

    private static void Serve(CoreWebView2 core, string html)
    {
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = core.Environment.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8");
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (true)
        {
            if (await condition()) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100);
        }
    }
    private static Task<bool> WaitUntilAsync(Func<bool> condition, int milliseconds) =>
        WaitUntilAsync(() => Task.FromResult(condition()), milliseconds);

    // A permission request from page script, resolved by the app's handler only.
    private static async Task<string> RequestPermissionAsync(CoreWebView2 web)
    {
        await web.ExecuteScriptAsync("window.__permission=null;Notification.requestPermission().then(p=>window.__permission=p,()=>window.__permission='error');");
        string result = "null";
        await WaitUntilAsync(async () => (result = await web.ExecuteScriptAsync("window.__permission")) != "null", 5000);
        return result;
    }

    private static async Task ResetNotificationPermissionAsync(CoreWebView2Profile profile)
    {
        // Back to "never asked": this removes state, it never grants.
        foreach (string origin in NotificationPermissionPolicy.InstagramOrigins)
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, origin, CoreWebView2PermissionState.Default);
    }

    internal static void RunCore(Action<bool, string> check)
    {
        const string www = "https://www.instagram.com/direct/inbox/";
        var allow = NotificationPermissionPolicy.ForMonitor(CoreWebView2PermissionKind.Notifications, www, enabled: true);
        check(allow.State == CoreWebView2PermissionState.Allow && allow.SavesInProfile,
            "monitor policy allows and persists Instagram notifications when enabled");
        check(NotificationPermissionPolicy.ForMonitor(CoreWebView2PermissionKind.Notifications, "https://instagram.com/", true).State ==
            CoreWebView2PermissionState.Allow, "monitor policy covers both Instagram origins");
        var disabled = NotificationPermissionPolicy.ForMonitor(CoreWebView2PermissionKind.Notifications, www, enabled: false);
        check(disabled.State == CoreWebView2PermissionState.Deny && !disabled.SavesInProfile,
            "monitor policy denies notifications when disabled without overwriting the profile");
        foreach (var kind in new[] { CoreWebView2PermissionKind.Microphone, CoreWebView2PermissionKind.Camera,
            CoreWebView2PermissionKind.Geolocation, CoreWebView2PermissionKind.ClipboardRead, CoreWebView2PermissionKind.UnknownPermission })
        {
            var other = NotificationPermissionPolicy.ForMonitor(kind, www, enabled: true);
            check(other.State == CoreWebView2PermissionState.Deny && !other.SavesInProfile, "monitor policy denies " + kind);
        }
        foreach (string origin in new[] { "https://evil.test/", "https://help.instagram.com/", "http://www.instagram.com/",
            "https://www.instagram.com:8443/", "https://instagram.com.evil.test/", "" })
            check(NotificationPermissionPolicy.ForMonitor(CoreWebView2PermissionKind.Notifications, origin, true).State ==
                CoreWebView2PermissionState.Deny, "monitor policy denies notification origin " + origin);
        check(NotificationPermissionPolicy.ForPrimaryNotifications(www, true).State == CoreWebView2PermissionState.Allow &&
            NotificationPermissionPolicy.ForPrimaryNotifications(www, false).State == CoreWebView2PermissionState.Deny &&
            NotificationPermissionPolicy.ForPrimaryNotifications("https://help.instagram.com/", true).State == CoreWebView2PermissionState.Deny,
            "primary notification policy follows setting and exact origins");
        check(NotificationPermissionPolicy.InstagramOrigins.SequenceEqual(new[] { "https://www.instagram.com", "https://instagram.com" }),
            "permission sync targets exact www and root origins");
    }

    internal static async Task RunMirrorAsync(Dispatcher dispatcher, Action<bool, string> check)
    {
        var owned = new List<NotificationService>();
        (NotificationService Service, Presenter Ui) Create(bool monitoring)
        {
            var ui = new Presenter();
            var service = new NotificationService(dispatcher, () => true, _ => { }, ui) { DirectInboxMonitoring = monitoring };
            owned.Add(service); return (service, ui);
        }
        var native = new InstagramNotification { Source = NotificationSource.NativeWebView, Title = "Alice", Body = "hello",
            Tag = "/direct/t/5/", ThreadUrl = "https://www.instagram.com/direct/t/5/", Type = InstagramNotificationType.DirectMessage };
        var generic = new InstagramNotification { Source = NotificationSource.NativeWebView };
        try
        {
            var mirrored = Create(true);
            mirrored.Service.Receive(native with { Emitter = DirectInboxMonitor.Emitter });
            mirrored.Service.Receive(native with { Emitter = WebViewService.PrimaryEmitter });
            var reversed = Create(false);
            reversed.Service.Receive(native with { Emitter = WebViewService.PrimaryEmitter });
            reversed.Service.Receive(native with { Emitter = DirectInboxMonitor.Emitter });
            var repeated = Create(true);
            repeated.Service.Receive(native with { Emitter = DirectInboxMonitor.Emitter });
            repeated.Service.Receive(native with { Emitter = DirectInboxMonitor.Emitter });
            var twoMessages = Create(true);
            foreach (string emitter in new[] { WebViewService.PrimaryEmitter, DirectInboxMonitor.Emitter, WebViewService.PrimaryEmitter, DirectInboxMonitor.Emitter })
                twoMessages.Service.Receive(native with { Emitter = emitter });
            var genericMirror = Create(true);
            genericMirror.Service.Receive(generic with { Emitter = DirectInboxMonitor.Emitter });
            genericMirror.Service.Receive(generic with { Emitter = WebViewService.PrimaryEmitter });
            await Task.Delay(NotificationService.EnrichmentMilliseconds + 300);
            check(mirrored.Ui.Popups == 1 && mirrored.Ui.Shows.Count == 1, "monitor and primary copies of one native message show one toast");
            check(reversed.Ui.Popups == 1, "primary then monitor copies of one native message show one toast");
            check(repeated.Ui.Popups == 2, "two identical native events from one controller remain two messages");
            check(twoMessages.Ui.Popups == 2, "two mirrored identical messages remain exactly two toasts");
            check(genericMirror.Ui.Popups == 1, "mirrored generic native waits once and shows once");
        }
        finally { foreach (var service in owned) service.Dispose(); }
    }

    internal static async Task RunPresenterAsync(Dispatcher dispatcher, Action<bool, string> check)
    {
        var events = new List<(LogEvent Name, Exception? Error, int? Code)>();
        var previous = LoggingService.DiagnosticObserver;
        LoggingService.DiagnosticObserver = (name, error, code) => { previous?.Invoke(name, error, code); events.Add((name, error, code)); };
        var sample = new InstagramNotification { Title = "Alice", Body = "hello", ThreadUrl = "https://www.instagram.com/direct/t/1/" };
        try
        {
            var notifier = new FakeNotifier();
            var presenter = new WindowsNotificationService(() => notifier);
            check(presenter.Show(sample, "0123456789abcdef", null, null, false) && notifier.Shown.Count == 1 &&
                events.Any(e => e.Name == LogEvent.WindowsNotificationSetting && e.Code == (int)NotificationSetting.Enabled) &&
                events.Any(e => e.Name == LogEvent.WindowsNotificationSubmitted),
                "real presenter logs enabled setting and submission");
            check(notifier.Shown[0].Tag == "0123456789abcdef" && notifier.Shown[0].Group == "instagram" && !notifier.Shown[0].SuppressPopup,
                "real presenter builds a popup toast with the delivery token");
            string conversationXml = notifier.Shown[0].Content.GetXml();
            check(conversationXml.Contains("<action", StringComparison.Ordinal) && conversationXml.Contains("action=mute", StringComparison.Ordinal) &&
                conversationXml.Contains("activationType=\"background\"", StringComparison.Ordinal),
                "a conversation's toast offers Mute without opening the window");
            presenter.Show(sample with { ThreadUrl = null }, "0123456789abcdee", null, null, false);
            check(!notifier.Shown[^1].Content.GetXml().Contains("<action", StringComparison.Ordinal),
                "a toast without a known conversation has no Mute button");
            notifier.Shown.Clear();
            foreach (var (setting, expected) in new[]
            {
                (NotificationSetting.DisabledForApplication, LogEvent.WindowsNotificationDisabledForApplication),
                (NotificationSetting.DisabledForUser, LogEvent.WindowsNotificationDisabledForUser),
                (NotificationSetting.DisabledByGroupPolicy, LogEvent.WindowsNotificationDisabledByGroupPolicy),
                (NotificationSetting.DisabledByManifest, LogEvent.WindowsNotificationDisabledByManifest)
            })
            {
                events.Clear(); notifier.Value = setting; notifier.Shown.Clear();
                check(!presenter.Show(sample, "0123456789abcdef", null, null, false) && notifier.Shown.Count == 0 &&
                    events.Any(e => e.Name == expected && e.Code == (int)setting) &&
                    !events.Any(e => e.Name == LogEvent.WindowsNotificationSubmitted),
                    "system disable is reported as " + expected + " and not counted as shown");
            }
            events.Clear(); notifier.Value = null;
            check(presenter.Show(sample, "0123456789abcdef", null, null, false) && notifier.Shown.Count == 1 &&
                events.Any(e => e.Name == LogEvent.WindowsNotificationSettingUnavailable && e.Error is not null),
                "unreadable Windows setting is logged and Show is still attempted");
            events.Clear(); notifier.Value = NotificationSetting.Enabled; notifier.ShowError = new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070490));
            check(!presenter.Show(sample, "0123456789abcdef", null, null, false) &&
                events.Any(e => e.Name == LogEvent.WindowsNotificationShowFailed && e.Error?.HResult == unchecked((int)0x80070490)) &&
                !events.Any(e => e.Name == LogEvent.WindowsNotificationSubmitted),
                "Show exception is logged with its HResult and reported as not shown");
            events.Clear();
            string? failed = null;
            presenter.Failed += token => failed = token;
            presenter.ReportDeliveryFailed("0123456789abcdef", new InvalidOperationException());
            check(failed == "0123456789abcdef" && events.Any(e => e.Name == LogEvent.WindowsNotificationDeliveryFailed),
                "ToastNotification.Failed is logged and forwarded");
            events.Clear();
            bool? userCanceled = null;
            presenter.Dismissed += (_, canceled) => userCanceled = canceled;
            presenter.ReportDismissed("0123456789abcdef", ToastDismissalReason.TimedOut);
            check(userCanceled == false && events.Any(e => e.Name == LogEvent.WindowsNotificationDismissed && e.Code == (int)ToastDismissalReason.TimedOut),
                "toast timeout is logged and keeps the notification lifecycle");

            // Coordinator + real presenter: a failure never crashes delivery and
            // a Windows Failed event retries once as text only.
            notifier.ShowError = null; notifier.Shown.Clear();
            var failing = new WindowsNotificationService(() => notifier);
            using (var coordinator = new NotificationService(dispatcher, () => true, _ => { }, failing))
            {
                coordinator.Receive(sample with { Source = NotificationSource.NativeWebView });
                await WaitUntilAsync(() => notifier.Shown.Count == 1, 3000);
                failing.ReportDeliveryFailed(notifier.Shown[0].Tag, null);
                await WaitUntilAsync(() => notifier.Shown.Count == 2, 3000);
                failing.ReportDeliveryFailed(notifier.Shown[1].Tag, null);
                await Task.Delay(300);
                check(notifier.Shown.Count == 2, "failed toast is retried exactly once");
                notifier.Value = NotificationSetting.DisabledForUser; notifier.Shown.Clear();
                coordinator.Receive(sample with { Source = NotificationSource.NativeWebView, Body = "while disabled" });
                await Task.Delay(300);
                check(notifier.Shown.Count == 0, "disabled Windows notifications produce no toast and no substitute");
            }
        }
        finally { LoggingService.DiagnosticObserver = previous; }
    }

    // Standalone monitor on the shared profile, starting from "never asked".
    internal static async Task RunMonitorAsync(MainWindow window, CoreWebView2 primary, Action<bool, string> check)
    {
        await ResetNotificationPermissionAsync(primary.Profile);
        await primary.ExecuteScriptAsync("localStorage.setItem('inbox-monitor-fixture','shared');");
        bool enabled = true;
        var ui = new Presenter();
        using var coordinator = new NotificationService(window.Dispatcher, () => enabled, _ => { }, ui) { DirectInboxMonitoring = true };
        var decisions = new List<(CoreWebView2PermissionKind Kind, CoreWebView2PermissionState State, bool Saves)>();
        var handled = new List<bool>();
        int baseline = DirectInboxMonitor.LiveCount;
        async Task<DirectInboxMonitor> StartAsync()
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var monitor = new DirectInboxMonitor(window.Dispatcher, coordinator, () => enabled);
            await monitor.StartAsync(primary, new WindowInteropHelper(window).Handle, core =>
            {
                Serve(core, InboxFixture);
                core.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
                // Registered after the monitor's own handlers: observes their result.
                core.PermissionRequested += (_, e) => decisions.Add((e.PermissionKind, e.State, e.SavesInProfile));
                core.NotificationReceived += (_, e) => handled.Add(e.Handled);
            });
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return monitor;
        }

        // 1. AppNotifications=true, no manual Allow anywhere.
        var monitor = await StartAsync();
        var web = monitor.Core!;
        check(await web.ExecuteScriptAsync("Notification.permission") == "\"default\"", "monitor starts from an ungranted profile permission");
        check(await RequestPermissionAsync(web) == "\"granted\"", "enabled monitor grants Instagram notification request without test Allow");
        var asked = decisions.Where(d => d.Kind == CoreWebView2PermissionKind.Notifications).ToList();
        check(asked.Count == 1 && asked[0].State == CoreWebView2PermissionState.Allow && asked[0].Saves,
            "monitor permission handler answered Allow and saved it in the profile");
        check(await NotificationPermissionPolicy.LogStateAsync(primary.Profile) % 10 == (int)CoreWebView2PermissionState.Allow,
            "profile retains the monitor-granted www.instagram.com permission");
        await web.ExecuteScriptAsync("window.monitorEvents=[];window.monitorNotice=new Notification('Monitor fixture',{body:'Delivered from the background inbox',tag:'/direct/t/456/',silent:true});for(const event of ['show','close'])monitorNotice.addEventListener(event,()=>monitorEvents.push(event));");
        await WaitUntilAsync(() => ui.Shows.Count > 0, 3000);
        await Task.Delay(300);
        check(handled.Count == 1 && handled[0] && ui.Popups == 1 && ui.Shows[0].Value.Source == NotificationSource.NativeWebView,
            "ungranted-profile monitor receives a web notification and shows exactly one toast");
        check(ui.Shows[0].Value.ThreadUrl == "https://www.instagram.com/direct/t/456/" && ui.Shows[0].Value.Silent &&
            await web.ExecuteScriptAsync("monitorEvents.includes('show')") == "true", "monitor native metadata and shown lifecycle retained");
        int removed = ui.Removed;
        await web.ExecuteScriptAsync("monitorNotice.close();");
        await Task.Delay(300);
        check(ui.Removed > removed, "monitor native close removes its existing notification");
        monitor.Dispose();
        check(!monitor.HandlersAttached && DirectInboxMonitor.LiveCount == baseline, "disposed monitor detaches every handler");

        // 2. AppNotifications=false: deny, no toast, no browser notification UI.
        enabled = false; decisions.Clear(); handled.Clear(); ui.Shows.Clear(); coordinator.Reset(false);
        await ResetNotificationPermissionAsync(primary.Profile);
        monitor = await StartAsync();
        web = monitor.Core!;
        check(await RequestPermissionAsync(web) != "\"granted\"", "disabled monitor does not grant notification request");
        asked = decisions.Where(d => d.Kind == CoreWebView2PermissionKind.Notifications).ToList();
        check(asked.Count == 1 && asked[0].State == CoreWebView2PermissionState.Deny && !asked[0].Saves,
            "disabled monitor answers Deny without saving it");
        check(await NotificationPermissionPolicy.LogStateAsync(primary.Profile) == 0, "background denial never overwrites the profile");
        await web.ExecuteScriptAsync("new Notification('Disabled fixture',{body:'must stay hidden'});");
        await Task.Delay(500);
        check(handled.Count == 0 && ui.Shows.Count == 0, "disabled monitor produces neither native event nor toast");
        // A stale grant while disabled (setting just flipped): the event is
        // consumed so WebView2 shows no UI, and the coordinator shows nothing.
        await NotificationPermissionPolicy.SyncAsync(primary.Profile, () => true);
        await web.ExecuteScriptAsync("new Notification('Disabled fixture',{body:'stale grant'});");
        await Task.Delay(500);
        check(handled.Count == 1 && handled[0] && ui.Shows.Count == 0, "disabled mode suppresses browser UI and Windows toast");
        await NotificationPermissionPolicy.SyncAsync(primary.Profile, () => false);
        check(await NotificationPermissionPolicy.LogStateAsync(primary.Profile) == 22, "disabled sync denies both origins");
        monitor.Dispose();
        await primary.ExecuteScriptAsync("localStorage.removeItem('inbox-monitor-fixture');");
    }

    // The production WebViewService path: settings sync, automatic monitor
    // start, restart/recovery and SPA routes, with a fake Windows presenter.
    internal static async Task RunServiceAsync(MainWindow window, SettingsService settings, Action<bool, string> check)
    {
        var ui = new Presenter();
        var host = new Grid();
        window.BrowserHost.Children.Add(host);
        settings.Current.AppNotifications = true;
        using var notifications = new NotificationService(window.Dispatcher, () => settings.Current.AppNotifications, _ => { }, ui);
        var service = new WebViewService(host, window, settings, notifications);
        TaskCompletionSource monitorLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource primaryLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticMonitorConfigure = core =>
        {
            var loaded = monitorLoaded;
            Serve(core, InboxFixture);
            core.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
        };
        void ConfigurePrimary(CoreWebView2 core)
        {
            Serve(core, PrimaryFixture);
            // Read at event time: each primary navigation gets a fresh signal.
            core.NavigationCompleted += (_, e) => { if (e.IsSuccess) primaryLoaded.TrySetResult(); };
        }
        async Task<DirectInboxMonitor> RunningMonitorAsync(string name)
        {
            await WaitUntilAsync(async () =>
            {
                if (service.DirectMonitor?.IsRunning != true) await service.RefreshDirectMonitorAsync();
                return service.DirectMonitor?.IsRunning == true;
            }, 10000);
            check(service.DirectMonitor?.IsRunning == true, name);
            await monitorLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return service.DirectMonitor!;
        }
        async Task RestartPrimaryAsync()
        {
            primaryLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            monitorLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await service.InitializeAsync(ConfigurePrimary);
            await primaryLoaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        int baseline = DirectInboxMonitor.LiveCount;
        try
        {
            await RestartPrimaryAsync();
            var primary = service.Core!;
            check(await NotificationPermissionPolicy.LogStateAsync(primary.Profile) == 11, "startup sync allows both Instagram origins");

            // 3. Primary away from /direct/inbox/: the hidden monitor still delivers.
            primaryLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Navigate("https://www.instagram.com/reels/");
            await primaryLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var monitor = await RunningMonitorAsync("service starts hidden monitor while primary is on Reels");
            check(DirectInboxMonitor.LiveCount == baseline + 1, "service runs exactly one monitor");
            await monitor.Core!.ExecuteScriptAsync("new Notification('Service sender',{body:'Background DM 1',tag:'/direct/t/789/'});");
            await WaitUntilAsync(() => ui.Popups == 1, 3000);
            // The primary page raises its own copy of the same message.
            await primary.ExecuteScriptAsync("new Notification('Service sender',{body:'Background DM 1',tag:'/direct/t/789/'});");
            await Task.Delay(NotificationService.EnrichmentMilliseconds + 300);
            check(ui.Popups == 1 && ui.Shows[0].Value.Body == "Background DM 1", "monitor DM plus primary mirror shows one toast");
            check(primary.Source == "https://www.instagram.com/reels/" && monitor.Core!.Source == DirectInboxMonitor.InboxUrl,
                "primary route is not moved to inbox or thread by the monitor");

            // 4a. Concurrent refreshes and primary SPA routes keep the same monitor.
            await Task.WhenAll(service.RefreshDirectMonitorAsync(), service.RefreshDirectMonitorAsync());
            await primary.ExecuteScriptAsync("history.pushState({},'','/explore/');");
            await Task.Delay(500);
            check(service.DirectMonitor == monitor && DirectInboxMonitor.LiveCount == baseline + 1,
                "refresh and primary SPA route change never create a second monitor");

            // 4b. Monitor restart after its own route block.
            monitorLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await monitor.Core!.ExecuteScriptAsync("history.pushState({},'','/direct/t/123/');");
            await WaitUntilAsync(() => !monitor.IsRunning, 3000);
            check(!monitor.IsRunning && !monitor.HandlersAttached && DirectInboxMonitor.LiveCount == baseline,
                "blocked monitor stops and releases its handlers");
            service.ExpireDirectMonitorBackoff();
            await Task.WhenAll(service.RefreshDirectMonitorAsync(), service.RefreshDirectMonitorAsync(), service.RefreshDirectMonitorAsync());
            check(DirectInboxMonitor.LiveCount <= baseline + 1, "concurrent restart requests create at most one monitor");
            var restarted = await RunningMonitorAsync("monitor restarts after reset");
            check(restarted != monitor && DirectInboxMonitor.LiveCount == baseline + 1, "restart replaces rather than adds a monitor");
            await restarted.Core!.ExecuteScriptAsync("new Notification('Service sender',{body:'Background DM 2',tag:'/direct/t/789/'});");
            await Task.Delay(800);
            check(ui.Popups == 2 && ui.Shows.Last().Value.Body == "Background DM 2", "restarted monitor delivers once without stale handlers");
            check(notifications.NativeCount(monitor) == 0 && notifications.NativeCount(restarted) == 1,
                "closed monitor released its native notifications; live monitor owns its own");
            string fromCrashedMonitor = ui.Shows.Last().Token;

            // 4c. Renderer crash of the monitor, then primary recovery.
            monitorLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { await restarted.Core!.CallDevToolsProtocolMethodAsync("Page.crash", "{}").WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
            await WaitUntilAsync(() => !restarted.IsRunning, 5000);
            check(!restarted.IsRunning && !restarted.HandlersAttached, "monitor process failure stops the monitor");
            check(notifications.NativeCount(restarted) == 0, "crashed monitor releases native notifications before closing");
            // Before the fix this reached ReportClicked on a closed controller:
            // an uncatchable access violation that terminated the app.
            var click = new Microsoft.Toolkit.Uwp.Notifications.ToastArguments();
            click.Add("notification", fromCrashedMonitor);
            notifications.Activate(click.ToString());
            check(true, "clicking a toast delivered by a closed monitor is safe");
            if (service.NeedsRecovery || service.Core is null) await RestartPrimaryAsync();
            else service.ExpireDirectMonitorBackoff();
            var recovered = await RunningMonitorAsync("monitor returns after process failure");
            check(DirectInboxMonitor.LiveCount == baseline + 1, "process recovery keeps a single monitor");
            await RestartPrimaryAsync();
            check(!recovered.IsRunning && !recovered.HandlersAttached, "primary reinitialization closes the old monitor");
            primary = service.Core!;
            var afterRecovery = await RunningMonitorAsync("monitor returns after primary reinitialization");
            check(DirectInboxMonitor.LiveCount == baseline + 1, "primary recovery keeps a single monitor");
            await afterRecovery.Core!.ExecuteScriptAsync("new Notification('Service sender',{body:'Background DM 3',tag:'/direct/t/789/'});");
            await Task.Delay(800);
            check(ui.Popups == 3 && ui.Shows.Last().Value.Body == "Background DM 3", "recovered monitor delivers exactly once");

            // C. Setting off/on and restart with a stale denial in the profile.
            settings.Current.AppNotifications = false;
            await service.ApplySettingsAsync();
            check(!afterRecovery.IsRunning && DirectInboxMonitor.LiveCount == baseline, "disabling notifications stops the monitor");
            check(await NotificationPermissionPolicy.LogStateAsync(primary.Profile) == 22 &&
                await primary.ExecuteScriptAsync("Notification.permission") == "\"denied\"", "disabling denies both origins");
            await primary.ExecuteScriptAsync("new Notification('Service sender',{body:'while disabled'});");
            await Task.Delay(500);
            check(ui.Popups == 3, "disabled service shows no toast");
            monitorLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.Current.AppNotifications = true;
            await service.ApplySettingsAsync();
            check(await NotificationPermissionPolicy.LogStateAsync(primary.Profile) == 11 &&
                await primary.ExecuteScriptAsync("Notification.permission") == "\"granted\"", "re-enabling allows both origins");
            await RunningMonitorAsync("re-enabling restarts the monitor");
            check(DirectInboxMonitor.LiveCount == baseline + 1, "re-enabling keeps a single monitor");
            foreach (string origin in NotificationPermissionPolicy.InstagramOrigins)
                await primary.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, origin, CoreWebView2PermissionState.Deny);
            await RestartPrimaryAsync();
            check(await NotificationPermissionPolicy.LogStateAsync(service.Core!.Profile) == 11, "restart repairs a stale denied profile permission");
            await service.ResetPermissionsAsync();
            check(await NotificationPermissionPolicy.LogStateAsync(service.Core!.Profile) == 11, "permission reset reapplies enabled state to both origins");
            var final = await RunningMonitorAsync("monitor runs after restart");
            await final.Core!.ExecuteScriptAsync("new Notification('Service sender',{body:'Background DM 4',tag:'/direct/t/789/'});");
            await Task.Delay(800);
            check(ui.Popups == 4 && DirectInboxMonitor.LiveCount == baseline + 1, "restarted app delivers background DM once");
        }
        finally
        {
            service.Dispose();
            window.BrowserHost.Children.Remove(host);
        }
        check(DirectInboxMonitor.LiveCount == baseline, "service disposal closes its monitor");
    }
}
