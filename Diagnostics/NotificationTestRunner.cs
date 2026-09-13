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
            await window.Web.ApplySettingsAsync(false);
            window.Web.SetBackground(true);
            Check(!web.IsSuspended && (!window.Web.MemoryApiAvailable || web.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal), "notifications enabled background stays normal and unsuspended");
            settings.Current.AppNotifications = false;
            await window.Web.ApplySettingsAsync(false);
            window.Web.SetBackground(true);
            Check(!window.Web.MemoryApiAvailable || web.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low, "notifications disabled retains low memory optimization");
            Check(await web.ExecuteScriptAsync("Notification.permission") == "\"denied\"", "notifications disabled denies page permission without startup failure");
            Check(await web.ExecuteScriptAsync("1+1") == "2", "WebView remains usable without notification permission");
        }
        catch (Exception error)
        {
            failure = error is InvalidOperationException ? checks.LastOrDefault(x => !x.Value).Key : error.GetType().Name;
            LoggingService.Write(LogEvent.UnexpectedException, error);
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = failure is null, failure, checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.PrepareForShutdown(); window.DisposeResources();
            Application.Current.Shutdown(failure is null ? 0 : 1);
        }
    }
}
