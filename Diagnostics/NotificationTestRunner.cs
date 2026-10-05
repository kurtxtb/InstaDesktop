using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using InstaDesktop.Models;
using InstaDesktop.Services;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop.Diagnostics;

// Offline fixtures only, in a fresh diagnostic profile. No account, network
// request, real toast or current user profile is needed for these checks.
internal static class NotificationTestRunner
{
    private sealed class ImageHandler : HttpMessageHandler
    {
        public required Func<CancellationToken, Task<HttpResponseMessage>> Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Respond(token);
    }
    private sealed class Presenter : IWindowsNotificationPresenter
    {
        public event Action<string, bool>? Dismissed;
        public event Action<string>? Failed { add { } remove { } }
        public List<(InstagramNotification Value, bool Replace, string Token)> Shows { get; } = new();
        public bool Available = true;
        public int Removed;
        public bool Show(InstagramNotification n, string token, string? avatar, string? image, bool replace)
        { if (!Available) return false; Shows.Add((n, replace, token)); return true; }
        public void Remove(string token) { Removed++; }
        public void Dismiss(string token) => Dismissed?.Invoke(token, true);
    }

    public static async Task RunAsync(MainWindow window, SettingsService settings, string output)
    {
        var checks = new Dictionary<string, bool>();
        string? failure = null;
        void Check(bool condition, string name)
        { checks[name] = condition; if (!condition) throw new InvalidOperationException(name); }
        try
        {
            Check(NotificationPolicy.DirectUrl("/direct/t/123/") == "https://www.instagram.com/direct/t/123/", "relative Direct URL normalized");
            Check(NotificationPolicy.DirectUrl("https://instagram.com/direct/t/123") == "https://www.instagram.com/direct/t/123/", "absolute Direct URL normalized");
            foreach (string bad in new[] { "javascript:alert(1)", "file:///a", "//evil.test/direct/t/123/", "https://instagram.com.evil.test/direct/t/123/",
                "https://user@instagram.com/direct/t/123/", "http://instagram.com/direct/t/123/", "/direct/t/abc/", "/direct/t/1/?next=https://evil.test",
                "/direct/t/1/#x", "/direct/t/%31/", "https://instagram.com:8443/direct/t/1/", "https://instagram.com/a/../direct/t/1/", "/direct/t/1/\\x" })
                Check(NotificationPolicy.DirectUrl(bad) is null, "reject URL " + bad);
            Check(NotificationPolicy.ImageUrl("https://scontent.cdninstagram.com/a.jpg") is not null &&
                NotificationPolicy.ImageUrl("https://cdninstagram.com.evil.test/a.jpg") is null &&
                NotificationPolicy.ImageUrl("http://127.0.0.1/a.jpg") is null, "image allowlist");
            const string origin = "https://www.instagram.com/";
            string json = JsonSerializer.Serialize(new { type = "instadesktop:notification", source = "dom", sequence = "1", threadUrl = "/direct/t/123/",
                title = "Alice <&\"", body = "Want to play?\n👩‍💻", unread = true });
            Check(NotificationPolicy.TryParse(origin, json, out var parsed) && parsed!.Body.Contains("👩‍💻"), "structured JSON escapes and emoji preserved");
            foreach (string bad in new[] { "null", "[]", "{", "{\"type\":true}", json.Replace("\"unread\":true", "\"unread\":false"),
                json.Replace("\"source\":\"dom\"", "\"source\":\"native\""), json.Replace("/direct/t/123/", "javascript:alert(1)"), new string('x', 17000) })
                Check(!NotificationPolicy.TryParse(origin, bad, out _), "invalid contract " + checks.Count);
            Check(!NotificationPolicy.TryParse("https://evil.test", json, out _), "untrusted message origin");
            Check(NotificationPolicy.Normalize(new InstagramNotification { Title = "", Body = "" }).Body == "You have a new message", "generic normalization");

            var now = DateTimeOffset.UtcNow;
            var native = new InstagramNotification { Title = "Alice", Body = "ok", Source = NotificationSource.NativeWebView };
            var dom = native with { Source = NotificationSource.DirectDom, ThreadUrl = "https://www.instagram.com/direct/t/1/", StateSequence = "1", Type = InstagramNotificationType.DirectMessage };
            var dedup = new NotificationDeduplicator();
            dedup.Remember(native, "first", now);
            Check(dedup.Find(dom, now.AddMilliseconds(500)) == "first", "native and DOM correlate");
            dedup.Remember(dom, "first", now.AddMilliseconds(500));
            Check(dedup.Find(dom, now.AddSeconds(1)) == "first", "same DOM sequence rejected");
            Check(dedup.Find(dom with { StateSequence = "2" }, now.AddSeconds(1)) is null, "different sequence with identical text survives");
            Check(dedup.Find(native, now.AddSeconds(5)) is null, "identical native message five seconds later survives");
            Check(dedup.Find(native, now.AddSeconds(1)) is null, "second native event inside correlation window survives");
            Check(dedup.Find(dom with { ThreadUrl = "https://www.instagram.com/direct/t/2/", Body = "different" }, now.AddSeconds(1)) is null, "different conversation survives");
            var tagged = native with { Tag = "conversation", Timestamp = now, HasSourceTimestamp = true };
            dedup.Clear(); dedup.Remember(tagged, "tag", now);
            Check(dedup.Find(tagged, now.AddSeconds(1)) == "tag", "same native tag timestamp and content rejected");
            Check(dedup.Find(tagged with { Timestamp = now.AddSeconds(5) }, now.AddSeconds(5)) is null, "reused conversation tag survives");
            dedup.Clear(); dedup.Remember(dom with { Id = "message1" }, "id", now);
            Check(dedup.Find(native with { Id = "message2" }, now.AddMilliseconds(10)) is null, "different strong IDs survive");
            for (int i = 0; i < 300; i++) dedup.Remember(dom with { Id = "id" + i }, "entry" + i, now);
            Check(dedup.Find(dom with { Id = "id0", StateSequence = "old" }, now.AddSeconds(31)) is null, "dedup expiration");

            var presenter = new Presenter();
            bool enabled = true;
            string? activated = null;
            using (var coordinator = new NotificationService(window.Dispatcher, () => enabled, path => activated = path, presenter))
            {
                coordinator.Receive(dom);
                coordinator.Receive(native);
                await Task.Delay(1500);
                Check(presenter.Shows.Count == 1 && presenter.Shows[0].Value.Source == NotificationSource.NativeWebView &&
                    presenter.Shows[0].Value.ThreadUrl == dom.ThreadUrl, "priority arbitration emits one enriched native payload");
                var arguments = new Microsoft.Toolkit.Uwp.Notifications.ToastArguments();
                arguments.Add("notification", presenter.Shows[0].Token);
                arguments.Add("thread", dom.ThreadUrl!);
                coordinator.Activate(arguments.ToString());
                Check(activated == dom.ThreadUrl, "toast activation returns validated thread");
                coordinator.Reset(false); presenter.Shows.Clear();
                coordinator.Receive(native);
                await Task.Delay(100);
                coordinator.Receive(dom);
                Check(presenter.Shows.Count(x => !x.Replace) == 1 && presenter.Shows.Last().Replace, "late DOM enrichment silently updates same toast");
                coordinator.Reset(false); presenter.Shows.Clear();
                coordinator.Receive(dom); enabled = false;
                await Task.Delay(1400);
                Check(presenter.Shows.Count == 0, "disable cancels pending delivery");
                enabled = true; coordinator.Reset(false); presenter.Shows.Clear();
                coordinator.SoundEnabled = () => false;
                coordinator.Receive(native);
                await Task.Delay(500);
                Check(presenter.Shows.Count == 1 && presenter.Shows[0].Value.Silent, "notification sound off shows the toast silently");
                coordinator.SoundEnabled = () => true;
                coordinator.Reset(false); presenter.Shows.Clear();

                // Hide message content: sender only, no text, no image.
                int presented = 0;
                coordinator.Presented += () => presented++;
                coordinator.HideContent = () => true;
                coordinator.Receive(native with { Type = InstagramNotificationType.DirectMessage, ImageUrl = "https://scontent.cdninstagram.com/x.jpg" });
                await Task.Delay(500);
                Check(presenter.Shows.Count == 1 && presenter.Shows[0].Value.Body == "Sent you a message" &&
                    presenter.Shows[0].Value.ImageUrl is null && presenter.Shows[0].Value.Title == native.Title,
                    "hidden content keeps the sender and drops text and image");
                Check(presented == 1, "a new toast is announced once (taskbar flash)");
                coordinator.HideContent = () => false;
                coordinator.Reset(false); presenter.Shows.Clear();

                // A muted conversation shows nothing; others still do.
                string? mutedThread = dom.ThreadUrl;
                coordinator.IsMuted = thread => thread == mutedThread;
                coordinator.Receive(dom);
                await Task.Delay(1500);
                Check(presenter.Shows.Count == 0, "a muted conversation shows no toast");
                coordinator.IsMuted = _ => false;
                coordinator.Reset(false); presenter.Shows.Clear();

                // The toast's Mute button: mutes the thread without opening the window.
                string? muteRequested = null;
                coordinator.MuteRequested += thread => muteRequested = thread;
                coordinator.Receive(native);
                await Task.Delay(500);
                activated = null;
                var mute = new Microsoft.Toolkit.Uwp.Notifications.ToastArguments();
                mute.Add("action", "mute");
                mute.Add("notification", presenter.Shows[0].Token);
                mute.Add("thread", "https://www.instagram.com/direct/t/987/");
                coordinator.Activate(mute.ToString());
                Check(muteRequested == (presenter.Shows[0].Value.ThreadUrl ?? "https://www.instagram.com/direct/t/987/") && activated is null,
                    "Mute mutes the conversation and does not open the window");
                coordinator.Reset(false); presenter.Shows.Clear(); enabled = false;
                coordinator.Receive(native);
                Check(presenter.Shows.Count == 0, "disabled mode ignores native candidates");
                enabled = true; presenter.Available = false;
                coordinator.Receive(native with { Body = "unavailable" });
                await Task.Delay(100);
                Check(presenter.Shows.Count == 0, "unavailable presenter does not crash coordinator");
            }
            using (var images = new NotificationImageCache())
            {
                Check(await images.GetAsync("file:///C:/invalid", default) is null, "invalid avatar is optional");
                using var cancelled = new System.Threading.CancellationTokenSource(); cancelled.Cancel();
                Check(await images.GetAsync("https://scontent.cdninstagram.com/expired.png", cancelled.Token) is null, "cancelled avatar is optional");
            }
            foreach (string mode in new[] { "http-error", "decode-error", "oversized", "timeout" })
            {
                using var images = new NotificationImageCache(new ImageHandler { Respond = async token =>
                {
                    if (mode == "timeout") await Task.Delay(10000, token);
                    var response = new HttpResponseMessage(mode == "http-error" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                        { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
                    response.Content.Headers.ContentType = new("image/png");
                    if (mode == "oversized") response.Content.Headers.ContentLength = 2 * 1024 * 1024;
                    return response;
                }});
                Check(await images.GetAsync("https://scontent.cdninstagram.com/" + mode + ".png", default) is null, "avatar " + mode + " degrades to text");
            }

            await CheckInboxEnrichmentAsync(window, Check);
            await CheckNotificationRecoveryAsync(window, Check);
            CheckButtonInboxState(Check);
            NotificationInboxEvidenceTests.Run(Check);
            await NotificationBadgeEnrichmentTests.RunAsync(window.Dispatcher, Check);
            await NotificationUnknownUnreadTests.RunAsync(window.Dispatcher, Check);
            await NotificationInboxRealDomTests.RunAsync(window.Dispatcher, Check);
            NotificationMonitorPermissionTests.RunCore(Check);
            UpdateTests.Run(Check);
            await NotificationMonitorPermissionTests.RunMirrorAsync(window.Dispatcher, Check);
            await NotificationMonitorPermissionTests.RunPresenterAsync(window.Dispatcher, Check);
            if (Environment.GetCommandLineArgs().Contains("--notification-core-only")) return;

            // Notifications disabled in C# prevents these synthetic DOM events
            // from producing Windows notifications. Enable just the JS observer.
            settings.Current.AppNotifications = false;
            var candidates = new List<InstagramNotification>();
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const string fixture = """
                <!doctype html><html><body>
                <a id="inbox" href="/direct/inbox/">Messages<span id="badge">3</span></a>
                <a id="row" href="/direct/t/123/" data-unread="true" data-unread-count="1">
                  <span data-conversation-name>Alice</span><span id="preview" data-message-preview>old message</span>
                </a></body></html>
                """;
            await window.Web.InitializeAsync(core =>
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) => e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(fixture)), 200, "OK", "Content-Type: text/html; charset=utf-8");
                core.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
                core.WebMessageReceived += (_, e) =>
                { if (NotificationPolicy.TryParse(e.Source, e.WebMessageAsJson, out var n)) candidates.Add(n!); };
            });
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var web = window.Web.Core!;
            async Task Execute(string script) { await web.ExecuteScriptAsync(script); await Task.Delay(800); }
            async Task Expect(string script, int count, string name)
            { candidates.Clear(); await Execute(script); Check(candidates.Count == count, name); }
            await Expect("window.__InstaDesktopNotifications.setEnabled(true);", 0, "initial unread rows and badge baseline silently");
            await Expect("document.querySelector('#badge').textContent='4';document.querySelector('#row').dataset.unreadCount='2';document.querySelector('#preview').textContent='hello';", 1, "new incoming DOM transition emits once");
            Check(candidates[0].Title == "Alice" && candidates[0].Body == "hello", "real DOM preview retained");
            await Expect("document.querySelector('#row').outerHTML=document.querySelector('#row').outerHTML;", 0, "same row rerender is silent");
            await Expect("document.querySelector('#preview').textContent='outgoing or status edit';", 0, "preview change without incoming evidence is silent");
            await Expect("document.querySelector('#preview').textContent='ok';document.querySelector('#row').dataset.unreadCount='3';document.querySelector('#badge').textContent='5';", 1, "first repeated message observed");
            await Expect("document.querySelector('#row').dataset.unreadCount='4';document.querySelector('#badge').textContent='6';", 1, "same text with increasing unread counter observed");
            await Expect("document.querySelector('[data-conversation-name]').textContent='Gaming Group';document.querySelector('#row').insertAdjacentHTML('beforeend','<span data-sender-name>Bob</span>');document.querySelector('#preview').textContent='group hello';document.querySelector('#row').dataset.unreadCount='5';document.querySelector('#badge').textContent='7';", 1, "explicit group sender transition observed");
            Check(candidates[0].Title == "Gaming Group" && candidates[0].Body == "Bob: group hello" && candidates[0].SenderName == "Bob", "explicit group sender and preview formatted");
            await Expect("history.pushState({},'', '/reels/');document.querySelector('#row').remove();window.dispatchEvent(new Event('instadesktop:navigation'));", 0, "SPA navigation baselines silently");
            await Expect("document.querySelector('#badge').textContent='8';", 1, "global inbox badge works on Reels");
            Check(candidates[0].Source == NotificationSource.UnreadBadge, "no guessed sender on badge fallback");
            await Expect("document.querySelector('#inbox').remove();", 0, "missing navigation does not become zero");
            await Expect("document.body.innerHTML='<a id=\"inbox\" href=\"/direct/inbox/\"><span id=\"badge\">10</span></a>';", 0, "rediscovered unread badge baselines silently");
            await Expect("document.body.outerHTML='<body><a href=\"/direct/inbox/\"><span id=\"badge\">10</span></a></body>';", 0, "root replacement with same count is silent");
            await Expect("document.querySelector('#badge').textContent='11';", 1, "observer survives root replacement");
            await Expect("window.__InstaDesktopNotifications.setEnabled(false);document.querySelector('#badge').textContent='12';", 0, "disabled observer stops work");
            await Expect("window.__InstaDesktopNotifications.setEnabled(true);", 0, "reenabling observer establishes new baseline");
            await Expect("window.__InstaDesktopNotifications.setEnabled(false);document.body.innerHTML='<a href=\"/direct/inbox/\"><span id=\"badge\"></span></a>';window.__InstaDesktopNotifications.setEnabled(true);setTimeout(()=>document.querySelector('#badge').textContent='3',100);", 0, "staged initial badge hydration is silent");
            await Task.Delay(400);
            await Expect("document.querySelector('#badge').textContent='4';", 1, "settled baseline accepts subsequent badge transition");
            await web.ExecuteScriptAsync("window.__InstaDesktopNotifications.setEnabled(false);");
            // Exercise real WebView notification lifecycle events against the fake
            // Windows presenter, without registering/showing a Windows toast.
            var nativePresenter = new Presenter();
            using (var coordinator = new NotificationService(window.Dispatcher, () => true, _ => { }, nativePresenter))
            {
                void NativeReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
                {
                    e.Handled = true;
                    coordinator.Receive(new InstagramNotification { Title = e.Notification.Title, Body = e.Notification.Body,
                        Tag = e.Notification.Tag, Source = NotificationSource.NativeWebView }, e.Notification);
                }
                web.NotificationReceived += NativeReceived;
                try
                {
                    await web.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, origin, CoreWebView2PermissionState.Allow);
                    await Execute("window.nativeEvents=[];window.nativeTest=new Notification('Fixture sender',{body:'Fixture message',tag:'fixture1'});for(const event of ['show','click','close'])nativeTest.addEventListener(event,()=>nativeEvents.push(event));");
                    Check(nativePresenter.Shows.Count == 1, "real WebView native notification event handled");
                    var arguments = new Microsoft.Toolkit.Uwp.Notifications.ToastArguments();
                    arguments.Add("notification", nativePresenter.Shows[0].Token);
                    coordinator.Activate(arguments.ToString());
                    await Task.Delay(500);
                    string events = await web.ExecuteScriptAsync("JSON.stringify(nativeEvents)");
                    Check(events.Contains("show", StringComparison.Ordinal), "native shown event reported");
                    Check(events.Contains("click", StringComparison.Ordinal), "native clicked event reported");
                    await Execute("window.nativeEvents=[];window.nativeTest=new Notification('Fixture sender',{body:'Dismiss test',tag:'dismiss'});nativeTest.addEventListener('close',()=>nativeEvents.push('close'));");
                    nativePresenter.Dismiss(nativePresenter.Shows.Last().Token);
                    await Task.Delay(500);
                    Check(await web.ExecuteScriptAsync("nativeEvents.includes('close')") == "true", "native dismissed event reported");
                    int removed = nativePresenter.Removed;
                    await Execute("window.nativeTest=new Notification('Fixture sender',{body:'Close request',tag:'fixture2'});");
                    await Execute("nativeTest.close();");
                    Check(nativePresenter.Removed > removed, "web close request removes corresponding presentation");
                }
                finally { web.NotificationReceived -= NativeReceived; }
            }
            settings.Current.AppNotifications = true;
            await window.Web.ApplySettingsAsync();
            await window.Web.ResetPermissionsAsync();
            Check(await web.ExecuteScriptAsync("Notification.permission") == "\"granted\"", "resetting permissions preserves enabled desktop notifications");
            window.Web.SetBackground(true);
            Check(!web.IsSuspended && (!window.Web.MemoryApiAvailable || web.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal), "notifications enabled background stays normal and unsuspended");
            settings.Current.AppNotifications = false;
            await window.Web.ApplySettingsAsync();
            await window.Web.ResetPermissionsAsync();
            Check(await web.ExecuteScriptAsync("Notification.permission") == "\"denied\"", "resetting permissions preserves disabled desktop notifications");
            window.Web.SetBackground(true);
            Check(!window.Web.MemoryApiAvailable || web.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low, "notifications disabled retains low memory optimization");
            Check(await web.ExecuteScriptAsync("Notification.permission") == "\"denied\"", "notifications disabled denies page permission without startup failure");
            Check(await web.ExecuteScriptAsync("1+1") == "2", "WebView remains usable without notification permission");
            await CheckInboxControllerAsync(window, web, Check);
            // Hidden monitor permission path without any test-side Allow, then the
            // real WebViewService lifecycle (last: it may crash a renderer).
            await NotificationMonitorPermissionTests.RunMonitorAsync(window, web, Check);
            await NotificationMonitorPermissionTests.RunServiceAsync(window, settings, Check);
        }
        catch (Exception error)
        {
            failure = error is InvalidOperationException ? checks.LastOrDefault(x => !x.Value).Key : error.GetType().Name;
            LoggingService.Write(LogEvent.UnexpectedException, error);
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = failure is null,
                scope = Environment.GetCommandLineArgs().Contains("--notification-core-only") ? "notification-core" : "notification-webview-integration",
                failure, checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.PrepareForShutdown(); window.DisposeResources();
            Application.Current.Shutdown(failure is null ? 0 : 1);
        }
    }

    private static async Task CheckInboxEnrichmentAsync(MainWindow window, Action<bool, string> check)
    {
        var generic = new InstagramNotification { Source = NotificationSource.NativeWebView };
        var alice = new InstagramNotification { Source = NotificationSource.DirectInbox, Title = "Alice", Body = "hello",
            ThreadUrl = "https://www.instagram.com/direct/t/1/", StateSequence = "inbox:1", Type = InstagramNotificationType.DirectMessage };
        check(NotificationPolicy.CanEnrich(generic with { Body = "你有一則新訊息" }) &&
            NotificationPolicy.CanEnrich(generic with { Body = "新しいメッセージがあります" }), "generic recognition is multilingual");
        check(!NotificationPolicy.CanEnrich(generic with { Body = "Alice liked your photo" }) &&
            !NotificationPolicy.CanEnrich(alice with { Source = NotificationSource.NativeWebView }), "meaningful native wording is preserved");

        string epoch = Guid.NewGuid().ToString();
        object Row(string id, string preview, bool unread, int? count, bool outgoing = false) => new
        { threadUrl = "/direct/t/" + id + "/", conversationName = "Alice", senderName = "", preview, avatarUrl = "", isUnread = unread, unreadCount = count, outgoing };
        string Snapshot(int seq, params object[] rows) => JsonSerializer.Serialize(new { type = "direct-inbox-snapshot", documentId = epoch, sequence = seq, ready = true, threads = rows });
        var state = new DirectInboxMonitor.InboxState();
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(1, Row("1", "old", false, 0))).Count == 0, "inbox initial snapshot is silent");
        var delta = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(2, Row("1", "hello", true, 1)));
        check(delta.Count == 1 && !delta[0].RequiresNativeConfirmation, "read to unread plus preview is strong inbox evidence");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(3, Row("1", "hello", true, 1))).Count == 0, "inbox rerender is silent");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(4, Row("1", "hello", true, 2))).Single().RequiresNativeConfirmation == false, "inbox identical preview counter increase survives");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(5, Row("1", "edited", true, 2))).Single().RequiresNativeConfirmation, "preview alone needs native confirmation");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(6, Row("1", "own message", true, 3, true))).Count == 0, "explicit outgoing inbox preview ignored");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(7, Row("1", "own message", true, 3, true), Row("2", "old unread", true, 1))).Count == 0, "new historical row below top is silent");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(8, Row("3", "old at top", true, 1), Row("1", "own message", true, 3, true))).Count == 0, "newly discovered top row also baselines silently");
        state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(9, Row("3", "changed while read", false, 0)));
        check(state.UncertainChange, "uncertain competing preview change vetoes attribution");
        epoch = Guid.NewGuid().ToString();
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(1, Row("1", "stale after reload", true, 9))).Count == 0, "monitor reload establishes silent baseline");
        foreach (string invalid in new[] { "{}", "[]", Snapshot(2, Row("1", "test", true, 0)), Snapshot(2, Row("1", "test", true, 1)).Replace("/direct/t/1/", "https://evil.test"), new string('x', 262145) })
        {
            bool rejected = false;
            try { state.Apply(DirectInboxMonitor.InboxUrl, invalid); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { rejected = true; }
            check(rejected, "invalid inbox snapshot rejected " + invalid.Length);
        }

        var owned = new List<NotificationService>();
        (NotificationService Service, Presenter Ui) Create()
        {
            var ui = new Presenter();
            var service = new NotificationService(window.Dispatcher, () => true, _ => { }, ui) { DirectInboxMonitoring = true };
            owned.Add(service); return (service, ui);
        }
        try
        {
            var forward = Create(); forward.Service.Receive(generic);
            var reverse = Create(); reverse.Service.Receive(alice);
            var timeout = Create(); timeout.Service.Receive(generic);
            var ambiguous = Create(); ambiguous.Service.Receive(generic);
            var weak = Create(); weak.Service.Receive(alice with { RequiresNativeConfirmation = true });
            var weakMatch = Create(); weakMatch.Service.Receive(generic);
            var failure = Create(); failure.Service.Receive(generic);
            var competing = Create(); competing.Service.Receive(generic); competing.Service.Receive(generic);
            var combined = Create(); combined.Service.Receive(generic with { Source = NotificationSource.UnreadBadge }); combined.Service.Receive(generic);
            var fromPrimary = Create(); fromPrimary.Service.Receive(generic); fromPrimary.Service.Receive(alice with { Source = NotificationSource.DirectDom }); fromPrimary.Service.ResetInboxEnrichment();
            var uncertain = Create(); uncertain.Service.Receive(generic); uncertain.Service.Receive(alice); uncertain.Service.NoteInboxUncertainty();
            var detailed = Create(); detailed.Service.Receive(alice with { Source = NotificationSource.NativeWebView });
            check(detailed.Ui.Shows.Count == 1, "already detailed native has no enrichment wait");
            var detailedAfterInbox = Create(); detailedAfterInbox.Service.Receive(alice);
            detailedAfterInbox.Service.Receive(alice with { Source = NotificationSource.NativeWebView });
            check(detailedAfterInbox.Ui.Shows.Count == 1, "detailed native bypasses an earlier inbox wait");
            await Task.Delay(300);
            check(forward.Ui.Shows.Count == 0, "generic held for short enrichment window");
            forward.Service.Receive(alice); reverse.Service.Receive(generic);
            ambiguous.Service.Receive(alice);
            weakMatch.Service.Receive(alice with { RequiresNativeConfirmation = true });
            failure.Service.Receive(alice); failure.Service.ResetInboxEnrichment();
            competing.Service.Receive(alice);
            combined.Service.Receive(alice with { Source = NotificationSource.DirectDom }); combined.Service.Receive(alice);
            await Task.Delay(300);
            ambiguous.Service.Receive(alice with { Title = "Bob", Body = "hi", ThreadUrl = "https://www.instagram.com/direct/t/2/" });
            await Task.Delay(2500);
            check(forward.Ui.Shows.Count == 1 && forward.Ui.Shows[0].Value.Title == "Alice" && forward.Ui.Shows[0].Value.ThreadUrl == alice.ThreadUrl, "generic plus inbox produces one enriched notification");
            check(reverse.Ui.Shows.Count == 1 && reverse.Ui.Shows[0].Value.Title == "Alice", "inbox preceding native also enriches once");
            check(detailedAfterInbox.Ui.Shows.Count == 1, "bypassed inbox wait cannot submit a second toast");
            check(timeout.Ui.Shows.Count == 1 && timeout.Ui.Shows[0].Value == NotificationPolicy.Normalize(generic), "enrichment timeout preserves original generic");
            check(ambiguous.Ui.Shows.Count(x => x.Value.Title == "Instagram") == 1 && ambiguous.Ui.Shows.Count == 3, "competing inbox changes never arbitrarily replace generic");
            check(weak.Ui.Shows.Count == 0 && weakMatch.Ui.Shows.Count == 1 && weakMatch.Ui.Shows[0].Value.Title == "Alice", "weak changes need one matching native event");
            check(failure.Ui.Shows.Count == 1 && failure.Ui.Shows[0].Value.Title == "Instagram", "monitor failure cannot swallow generic");
            check(competing.Ui.Shows.Count(x => x.Value.Title == "Instagram") == 2, "two generic events cannot claim one inbox change");
            check(combined.Ui.Shows.Count == 1 && combined.Ui.Shows[0].Value.Title == "Alice", "badge primary DOM native and monitor share one delivery");
            check(fromPrimary.Ui.Shows.Count == 1 && fromPrimary.Ui.Shows[0].Value.Title == "Alice", "existing primary Direct evidence remains usable when monitor fails");
            check(uncertain.Ui.Shows.Any(x => x.Value.Title == "Instagram"), "unclassified competing change preserves generic");
        }
        finally { foreach (var service in owned) service.Dispose(); }
    }

    private static void CheckButtonInboxState(Action<bool, string> check)
    {
        string epoch = Guid.NewGuid().ToString();
        string Snapshot(int sequence, string preview, bool? unread, string rowKey = "name:小明") => JsonSerializer.Serialize(new
        {
            type = "direct-inbox-snapshot", documentId = epoch, sequence, ready = true,
            threads = new[] { new { threadUrl = "", rowKey, conversationName = "小明", senderName = "小明",
                preview, avatarUrl = "", isUnread = unread, unreadCount = (int?)null, outgoing = false } }
        });
        var state = new DirectInboxMonitor.InboxState();
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(1, "舊訊息", null)).Count == 0,
            "button inbox row establishes a silent identity without thread URL");
        var message = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(2, "晚點一起吃飯？ 👋", true)).Single();
        check(message.Title == "小明" && message.SenderName == "小明" && message.Body == "晚點一起吃飯？ 👋" &&
            message.ThreadUrl is null && message.ConversationKey == "name:小明" && !message.RequiresNativeConfirmation,
            "button inbox incoming delta retains exact sender and message without fabricated URL");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(3, "晚點一起吃飯？ 👋", true)).Count == 0,
            "button inbox rerender cannot resend message");
        string bridge = JsonSerializer.Serialize(new { type = "instadesktop:notification", source = "dom", sequence = "page:1",
            rowKey = "name:小明", unread = true, title = "小明", senderName = "小明", body = "晚點一起吃飯？ 👋" });
        check(NotificationPolicy.TryParse(DirectInboxMonitor.InboxUrl, bridge, out var parsed) &&
            parsed!.ThreadUrl is null && parsed.ConversationKey == message.ConversationKey && parsed.Body == message.Body,
            "primary DOM bridge accepts identified button row with sender and content");
        check(!NotificationPolicy.TryParse(DirectInboxMonitor.InboxUrl,
            bridge.Replace(JsonSerializer.Serialize("name:小明"), JsonSerializer.Serialize("javascript:alert(1)")), out _),
            "invalid button row identity is rejected");
        bool rejected = false;
        try { state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(4, "unsafe", true, "")); }
        catch (JsonException) { rejected = true; }
        check(rejected, "route-less snapshot requires validated row identity");
    }

    private static async Task CheckNotificationRecoveryAsync(MainWindow window, Action<bool, string> check)
    {
        var inbox = new InstagramNotification { Source = NotificationSource.DirectInbox, Title = "Recovery fixture",
            Body = "New message", ThreadUrl = "https://www.instagram.com/direct/t/123/", StateSequence = "recovery:1",
            Type = InstagramNotificationType.DirectMessage };
        var owned = new List<NotificationService>();
        (NotificationService Service, Presenter Ui) Create()
        {
            var ui = new Presenter();
            var service = new NotificationService(window.Dispatcher, () => true, _ => { }, ui) { DirectInboxMonitoring = true };
            owned.Add(service); return (service, ui);
        }
        try
        {
            var nativeAfterReset = Create();
            nativeAfterReset.Service.Receive(inbox);
            nativeAfterReset.Service.ResetInboxEnrichment();
            nativeAfterReset.Service.Receive(inbox with { Source = NotificationSource.NativeWebView, StateSequence = null });
            check(nativeAfterReset.Ui.Shows.Count == 1, "monitor reset cannot suppress matching native notification");
            nativeAfterReset.Service.Receive(inbox with { Source = NotificationSource.DirectDom, StateSequence = "dom:late" });
            var pageAfterReset = Create();
            pageAfterReset.Service.Receive(inbox);
            pageAfterReset.Service.ResetInboxEnrichment();
            pageAfterReset.Service.Receive(inbox with { Source = NotificationSource.PageNotification, StateSequence = "page:1" });
            var latePage = Create();
            var lateDom = Create();
            latePage.Service.Receive(inbox with { RequiresNativeConfirmation = true });
            lateDom.Service.Receive(inbox with { RequiresNativeConfirmation = true });
            await Task.Delay(NotificationService.EnrichmentMilliseconds + 150);
            check(pageAfterReset.Ui.Shows.Count == 1, "monitor reset cannot suppress matching page notification");
            check(latePage.Ui.Shows.Count == 0 && lateDom.Ui.Shows.Count == 0, "unconfirmed inbox evidence expires without popup");
            latePage.Service.Receive(inbox with { Source = NotificationSource.PageNotification, StateSequence = "page:2" });
            lateDom.Service.Receive(inbox with { Source = NotificationSource.DirectDom, StateSequence = "dom:1" });
            await Task.Delay(NotificationService.EnrichmentMilliseconds + 200);
            check(latePage.Ui.Shows.Count == 1 && !latePage.Ui.Shows[0].Value.RequiresNativeConfirmation,
                "late matching page notification resumes expired inbox delivery once");
            check(lateDom.Ui.Shows.Count == 1 && !lateDom.Ui.Shows[0].Value.RequiresNativeConfirmation,
                "late strong primary DOM evidence resumes expired inbox delivery once");
            check(nativeAfterReset.Ui.Shows.Count(x => !x.Replace) == 1,
                "recovered native notification still deduplicates late primary DOM evidence");
        }
        finally { foreach (var service in owned) service.Dispose(); }
    }

    private static async Task CheckInboxControllerAsync(MainWindow window, CoreWebView2 primary, Action<bool, string> check)
    {
        var ui = new Presenter();
        using var coordinator = new NotificationService(window.Dispatcher, () => true, _ => { }, ui) { DirectInboxMonitoring = true };
        using var monitor = new DirectInboxMonitor(window.Dispatcher, coordinator, () => true);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new List<string>();
        string originalSource = primary.Source;
        await primary.ExecuteScriptAsync("localStorage.setItem('inbox-monitor-fixture','shared');");
        await monitor.StartAsync(primary, new WindowInteropHelper(window).Handle, core =>
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => e.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes("""
                    <!doctype html><html><body><a href="/direct/inbox/">Inbox</a>
                    <a id="row" href="/direct/t/123/" data-unread="false" data-unread-count="0">
                    <span data-conversation-name>Alice</span><span id="preview" data-message-preview>old</span></a>
                    </body></html>
                    """)), 200, "OK", "Content-Type: text/html; charset=utf-8");
            core.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
            core.WebMessageReceived += (_, e) =>
            {
                snapshots.Add(e.WebMessageAsJson);
                if (e.WebMessageAsJson.Contains("direct-inbox-snapshot", StringComparison.Ordinal)) firstSnapshot.TrySetResult();
            };
        });
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var web = monitor.Core!;
        check(await web.ExecuteScriptAsync("Boolean(window.__InstaDesktopInboxMonitor)") == "true", "monitor-specific script initialized");
        await firstSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(monitor.IsRunning && web.IsMuted && primary.Source == originalSource && await web.ExecuteScriptAsync("document.hidden") == "true", "hidden muted monitor leaves primary route unchanged");
        check(web.Profile.ProfileName == primary.Profile.ProfileName && await web.ExecuteScriptAsync("localStorage.getItem('inbox-monitor-fixture')") == "\"shared\"", "monitor reuses actual primary profile storage");
        check(snapshots.Any(s => s.Contains("direct-inbox-snapshot", StringComparison.Ordinal)) && ui.Shows.Count == 0, "hidden monitor emits silent initial snapshot");
        check(await web.ExecuteScriptAsync("typeof window.__InstaDesktopNotifications") == "\"undefined\"", "monitor does not inject primary notification bridge");
        // No test-side Allow here: the native path is verified from an ungranted
        // profile by NotificationMonitorPermissionTests. Keep native events off so
        // this section proves the DOM-only path alone.
        await NotificationPermissionPolicy.SyncAsync(primary.Profile, () => false);
        await web.ExecuteScriptAsync("document.body.outerHTML=document.body.outerHTML;");
        await Task.Delay(700);
        check(ui.Shows.Count == 0, "monitor root replacement alone is silent");
        await web.ExecuteScriptAsync("document.querySelector('#row').dataset.unread='true';document.querySelector('#row').dataset.unreadCount='1';document.querySelector('#preview').textContent='monitor hello';");
        await Task.Delay(3600);
        check(ui.Shows.Count == 1 && ui.Shows[0].Value.Body == "monitor hello", "hidden monitor DOM-only transition uses existing coordinator");
        check(web.Source == DirectInboxMonitor.InboxUrl && primary.Source == originalSource, "enrichment never navigates either controller into thread");
        await web.ExecuteScriptAsync("history.pushState({},'', '/direct/t/123/');");
        await Task.Delay(300);
        check(!monitor.IsRunning && primary.Source == originalSource, "monitor blocks SPA thread navigation and disposes");
        await primary.ExecuteScriptAsync("localStorage.removeItem('inbox-monitor-fixture');");
        using var cancelled = new DirectInboxMonitor(window.Dispatcher, coordinator, () => true);
        var creating = cancelled.StartAsync(primary, new WindowInteropHelper(window).Handle);
        cancelled.Dispose();
        await creating;
        check(!cancelled.IsRunning && cancelled.Core is null, "controller completing after disposal is closed");
    }
}
