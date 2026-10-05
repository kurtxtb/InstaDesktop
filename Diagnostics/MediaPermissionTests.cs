using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using InstaDesktop.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace InstaDesktop.Diagnostics;

// Explicit --media-permission-test mode only. Offline fixtures, an isolated
// diagnostic profile and Chromium's virtual capture devices: no account, call,
// real camera/microphone or the user's profile is touched. The fake-UI flag is
// never used, so every grant comes from a real PermissionRequested decision.
internal static class MediaPermissionTests
{
    internal const string FakeDevices = "--use-fake-device-for-media-stream";
    private const string Fixture = "<!doctype html><html><body><p>media fixture</p></body></html>";
    private const string Www = "https://www.instagram.com/direct/inbox/";
    private const string Root = "https://instagram.com/direct/inbox/";

    internal static void Serve(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = core.Environment.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(Fixture)), 200, "OK", "Content-Type: text/html; charset=utf-8");
    }

    internal static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (true)
        {
            if (await condition()) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100);
        }
    }

    // Returns "ok:audio/live,video/live" or "err:<DOMException name>" (never a message).
    internal static async Task<string> CaptureAsync(CoreWebView2 core, bool audio, bool video, int timeout = 8000)
    {
        await core.ExecuteScriptAsync("window.__g=null;window.__streams=window.__streams||[];navigator.mediaDevices.getUserMedia({audio:" +
            (audio ? "true" : "false") + ",video:" + (video ? "true" : "false") + "}).then(s=>{__streams.push(s);__g='ok:'+s.getTracks().map(t=>t.kind+'/'+t.readyState).join(',')},e=>__g='err:'+(e&&e.name||'unknown'));");
        string result = "null";
        await WaitUntilAsync(async () => (result = await core.ExecuteScriptAsync("window.__g")) != "null", timeout);
        return result == "null" ? "pending" : JsonSerializer.Deserialize<string>(result)!;
    }

    internal static async Task<string> TracksAsync(CoreWebView2 core) => JsonSerializer.Deserialize<string>(
        await core.ExecuteScriptAsync("(window.__streams||[]).flatMap(s=>s.getTracks()).map(t=>t.kind+'/'+t.readyState).join(',')"))!;

    internal static async Task StopTracksAsync(CoreWebView2 core) =>
        await core.ExecuteScriptAsync("(window.__streams||[]).forEach(s=>s.getTracks().forEach(t=>t.stop()));window.__streams=[];");

    internal static async Task<string> QueryAsync(CoreWebView2 core, string name)
    {
        await core.ExecuteScriptAsync("window.__q=null;navigator.permissions.query({name:'" + name + "'}).then(p=>__q=p.state,e=>__q='err:'+e.name);");
        string result = "null";
        await WaitUntilAsync(async () => (result = await core.ExecuteScriptAsync("window.__q")) != "null", 3000);
        return result == "null" ? "pending" : JsonSerializer.Deserialize<string>(result)!;
    }

    internal static async Task<CoreWebView2PermissionState> SavedAsync(CoreWebView2Profile profile, CoreWebView2PermissionKind kind, string origin)
    {
        var saved = await profile.GetNonDefaultPermissionSettingsAsync();
        return saved.FirstOrDefault(p => p.PermissionKind == kind &&
            string.Equals(p.PermissionOrigin.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.PermissionState
            ?? CoreWebView2PermissionState.Default;
    }

    internal static async Task NavigateAsync(CoreWebView2 core, string url)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess) loaded.TrySetResult(); }
        core.NavigationCompleted += Done;
        try { core.Navigate(url); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { core.NavigationCompleted -= Done; }
    }

    // Raw SDK/Runtime behavior with a test-owned handler, independent of app
    // code. These are recorded facts, not app assertions.
    internal static string? ProgressPath;
    private static void Progress(Dictionary<string, string> facts, string step)
    {
        facts["progress"] = step;
        if (ProgressPath is { } path) try { File.WriteAllText(path, JsonSerializer.Serialize(facts)); } catch (IOException) { }
    }

    internal static async Task<Dictionary<string, string>> ProbeRuntimeAsync(MainWindow window)
    {
        var facts = new Dictionary<string, string>();
        var host = new Grid();
        window.BrowserHost.Children.Add(host);
        var view = new WebView2();
        host.Children.Add(view);
        CoreWebView2Controller? second = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppPaths.Root, "ProbeData"),
                options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = FakeDevices });
            Progress(facts, "ensure");
            await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
            var core = view.CoreWebView2;
            Serve(core);
            facts["runtime"] = environment.BrowserVersionString;
            var answer = (State: CoreWebView2PermissionState.Deny, Saves: true);
            var events = new List<string>();
            core.PermissionRequested += (_, e) =>
            {
                events.Add(e.PermissionKind + (e.IsUserInitiated ? "/gesture" : "/nogesture"));
                e.State = answer.State;
                e.SavesInProfile = answer.Saves;
            };
            var profile = core.Profile;
            const string origin = "https://www.instagram.com";
            await NavigateAsync(core, Www);
            Progress(facts, "query.initial");
            facts["query.initial"] = await QueryAsync(core, "microphone");

            // F1: Deny saved by default.
            answer = (CoreWebView2PermissionState.Deny, true);
            Progress(facts, "F1.deny-default-save.first");
            facts["F1.deny-default-save.first"] = await CaptureAsync(core, true, false) + " events=" + events.Count;
            facts["F1.saved"] = (await SavedAsync(profile, CoreWebView2PermissionKind.Microphone, origin)).ToString();
            events.Clear(); answer = (CoreWebView2PermissionState.Allow, true);
            Progress(facts, "F1.second-request-with-allow-handler");
            facts["F1.second-request-with-allow-handler"] = await CaptureAsync(core, true, false) + " events=" + events.Count;
            Progress(facts, "F1.query");
            facts["F1.query"] = await QueryAsync(core, "microphone");
            await StopTracksAsync(core);

            // F2: Deny not saved.
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Default);
            await NavigateAsync(core, Www);
            events.Clear(); answer = (CoreWebView2PermissionState.Deny, false);
            Progress(facts, "F2.deny-nosave.first");
            facts["F2.deny-nosave.first"] = await CaptureAsync(core, true, false) + " events=" + events.Count;
            facts["F2.saved"] = (await SavedAsync(profile, CoreWebView2PermissionKind.Microphone, origin)).ToString();
            events.Clear(); answer = (CoreWebView2PermissionState.Allow, true);
            Progress(facts, "F2.second-request");
            facts["F2.second-request"] = await CaptureAsync(core, true, false) + " events=" + events.Count;
            facts["F2.saved-after-allow"] = (await SavedAsync(profile, CoreWebView2PermissionKind.Microphone, origin)).ToString();

            // F3: saved Allow, new request.
            events.Clear(); answer = (CoreWebView2PermissionState.Deny, false);
            Progress(facts, "F3.saved-allow-new-request");
            facts["F3.saved-allow-new-request"] = await CaptureAsync(core, true, false) + " events=" + events.Count;
            Progress(facts, "F3.query");
            facts["F3.query"] = await QueryAsync(core, "microphone");

            // F4: revoke a live stream through the profile.
            Progress(facts, "F4.tracks-before");
            facts["F4.tracks-before"] = await TracksAsync(core);
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Deny);
            await Task.Delay(2000);
            Progress(facts, "F4.tracks-after-profile-deny");
            facts["F4.tracks-after-profile-deny"] = await TracksAsync(core);
            Progress(facts, "F4.query-after-profile-deny");
            facts["F4.query-after-profile-deny"] = await QueryAsync(core, "microphone");
            events.Clear(); answer = (CoreWebView2PermissionState.Allow, true);
            Progress(facts, "F4.new-request-after-profile-deny");
            facts["F4.new-request-after-profile-deny"] = await CaptureAsync(core, true, false) + " events=" + events.Count;
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Default);
            await Task.Delay(1000);
            Progress(facts, "F4.tracks-after-profile-default");
            facts["F4.tracks-after-profile-default"] = await TracksAsync(core);
            events.Clear(); answer = (CoreWebView2PermissionState.Deny, false);
            Progress(facts, "F4.new-request-after-default");
            facts["F4.new-request-after-default"] = await CaptureAsync(core, true, false) + " events=" + events.Count;
            await StopTracksAsync(core);

            // F4b: saved Allow with a live stream, revoked to Default only.
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Allow);
            await NavigateAsync(core, Www);
            Progress(facts, "F4b");
            facts["F4b.capture"] = await CaptureAsync(core, true, false);
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Default);
            await Task.Delay(2000);
            facts["F4b.tracks-after-allow-to-default"] = await TracksAsync(core);
            await StopTracksAsync(core);
            // F4c: request-only Allow with a live stream, then profile Deny.
            await NavigateAsync(core, Www);
            events.Clear(); answer = (CoreWebView2PermissionState.Allow, false);
            facts["F4c.capture-nosave-allow"] = await CaptureAsync(core, true, false) + " events=" + events.Count;
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Deny);
            await Task.Delay(2000);
            facts["F4c.tracks-after-deny"] = await TracksAsync(core);
            await StopTracksAsync(core);

            // F5: combined request.
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Default);
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Camera, origin, CoreWebView2PermissionState.Default);
            events.Clear(); answer = (CoreWebView2PermissionState.Allow, false);
            Progress(facts, "F5.audio-video");
            facts["F5.audio-video"] = await CaptureAsync(core, true, true) + " events=" + string.Join("+", events);
            await StopTracksAsync(core);

            // F6: root origin is a separate permission origin.
            await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Allow);
            await NavigateAsync(core, Root);
            events.Clear(); answer = (CoreWebView2PermissionState.Deny, false);
            Progress(facts, "F6.root-with-www-allow");
            facts["F6.root-with-www-allow"] = await CaptureAsync(core, true, false) + " events=" + events.Count;

            // F7: second controller on the same profile with a saved Allow
            // (the hidden inbox monitor shape): does it still raise the event?
            await NavigateAsync(core, Www);
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = profile.ProfileName;
            second = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).Handle, options);
            second.IsVisible = false;
            second.Bounds = new Rectangle(0, 0, 800, 600);
            var hidden = second.CoreWebView2;
            Serve(hidden);
            var hiddenEvents = new List<string>();
            hidden.PermissionRequested += (_, e) => { hiddenEvents.Add(e.PermissionKind.ToString()); e.State = CoreWebView2PermissionState.Deny; e.SavesInProfile = false; };
            await NavigateAsync(hidden, Www);
            Progress(facts, "F7.hidden-saved-allow");
            facts["F7.hidden-saved-allow"] = await CaptureAsync(hidden, true, false) + " events=" + hiddenEvents.Count;
            await StopTracksAsync(hidden);
            await hidden.ExecuteScriptAsync("Object.defineProperty(Navigator.prototype,'mediaDevices',{configurable:false,get(){return undefined}});");
            Progress(facts, "F7.hidden-mediaDevices-removed");
            facts["F7.hidden-mediaDevices-removed"] = await hidden.ExecuteScriptAsync("typeof navigator.mediaDevices");

            // F9: a deferred request completed (Deny, not saved) or left pending
            // right before its WebView closes: is anything saved?
            foreach (string mode in new[] { "complete-then-close", "close-pending", "complete-deferred" })
            {
                await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Default);
                var extra = new WebView2();
                host.Children.Add(extra);
                await extra.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
                var temp = extra.CoreWebView2;
                Serve(temp);
                CoreWebView2Deferral? held = null;
                CoreWebView2PermissionRequestedEventArgs? heldArgs = null;
                temp.PermissionRequested += (_, e) => { heldArgs = e; held = e.GetDeferral(); };
                await NavigateAsync(temp, Www);
                await temp.ExecuteScriptAsync("navigator.mediaDevices.getUserMedia({audio:true}).then(()=>{},()=>{});");
                await WaitUntilAsync(() => Task.FromResult(held is not null), 5000);
                if (mode != "close-pending" && heldArgs is not null)
                {
                    heldArgs.SavesInProfile = false;
                    heldArgs.State = CoreWebView2PermissionState.Deny;
                    held!.Complete();
                }
                if (mode == "complete-deferred") await Task.Delay(1000);
                extra.Dispose();
                host.Children.Remove(extra);
                await Task.Delay(1000);
                Progress(facts, "F9." + mode);
                facts["F9." + mode] = (await SavedAsync(profile, CoreWebView2PermissionKind.Microphone, origin)).ToString();
            }

            // F8: Low memory target with a live stream.
            answer = (CoreWebView2PermissionState.Allow, false);
            Progress(facts, "F8.capture");
            facts["F8.capture"] = await CaptureAsync(core, true, true);
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                await Task.Delay(3000);
                Progress(facts, "F8.tracks-after-low");
                facts["F8.tracks-after-low"] = await TracksAsync(core) + " suspended=" + core.IsSuspended;
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch (Exception error) { facts["F8.error"] = error.GetType().Name; }
            await StopTracksAsync(core);
        }
        catch (Exception error) { facts["probe-error"] = error.GetType().Name + " HResult=" + error.HResult; }
        finally
        {
            try { second?.Close(); } catch (Exception) { }
            view.Dispose();
            window.BrowserHost.Children.Remove(host);
        }
        return facts;
    }

    private sealed class Presenter : IWindowsNotificationPresenter
    {
        public event Action<string, bool>? Dismissed { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public bool Show(Models.InstagramNotification value, string token, string? avatar, string? image, bool replace) => true;
        public void Remove(string token) { }
    }

    private const CoreWebView2PermissionKind Mic = CoreWebView2PermissionKind.Microphone;
    private const CoreWebView2PermissionKind Cam = CoreWebView2PermissionKind.Camera;

    // Script runner for the main document or an iframe (CoreWebView2Frame).
    private static async Task<string> CaptureWithAsync(Func<string, Task<string>> run, bool audio, bool video, int timeout = 8000)
    {
        await run("window.__g=null;window.__streams=window.__streams||[];navigator.mediaDevices.getUserMedia({audio:" + (audio ? "true" : "false") +
            ",video:" + (video ? "true" : "false") + "}).then(s=>{__streams.push(s);__g='ok:'+s.getTracks().map(t=>t.kind+'/'+t.readyState).join(',')},e=>__g='err:'+(e&&e.name||'unknown'));");
        string result = "null";
        await WaitUntilAsync(async () => (result = await run("window.__g")) != "null", timeout);
        return result == "null" ? "pending" : JsonSerializer.Deserialize<string>(result)!;
    }

    internal static void RunPolicy(Action<bool, string> check)
    {
        const string www = "https://www.instagram.com/direct/t/1/";
        foreach (var kind in new[] { Mic, Cam })
        {
            check(MediaPermissionPolicy.ForPrimary(kind, www, false) is { Prompt: false, Reason: MediaPermissionPolicy.Reason.SettingOff },
                "policy: switch off denies " + kind);
            check(MediaPermissionPolicy.ForPrimary(kind, www, true).Prompt && MediaPermissionPolicy.ForPrimary(kind, "https://instagram.com/", true).Prompt,
                "policy: switch on asks for www and root " + kind);
            check(!MediaPermissionPolicy.ForMonitor(kind, www).Prompt, "policy: monitor never asks " + kind);
        }
        foreach (string origin in new[] { "https://help.instagram.com/", "https://about.instagram.com/", "http://www.instagram.com/",
            "https://www.instagram.com:8443/", "https://user@www.instagram.com/", "https://instagram.com.evil.test/", "https://evilinstagram.com/",
            "https://www.facebook.com/", "https://www.messenger.com/", "", "not a url" })
            check(MediaPermissionPolicy.ForPrimary(Mic, origin, true) is { Prompt: false, Reason: MediaPermissionPolicy.Reason.UntrustedOrigin },
                "policy: media origin rejected " + origin);
        check(!MediaPermissionPolicy.ForPrimary(CoreWebView2PermissionKind.Geolocation, www, true).Prompt, "policy: only media kinds");
        check(MediaPermissionPolicy.Origins.SequenceEqual(new[] { "https://www.instagram.com", "https://instagram.com" }), "policy: exact origins");
        check(MediaPermissionPolicy.OriginCategory(www) == 1 && MediaPermissionPolicy.OriginCategory("https://instagram.com/") == 2 &&
            MediaPermissionPolicy.OriginCategory("https://help.instagram.com/") == 3 && MediaPermissionPolicy.OriginCategory("https://www.facebook.com/") == 4 &&
            MediaPermissionPolicy.OriginCategory("https://evil.test/") == 5 && MediaPermissionPolicy.OriginCategory("https://instagram.com:8443/") == 7 &&
            MediaPermissionPolicy.OriginCategory("http://instagram.com/") == 8 && MediaPermissionPolicy.OriginCategory(null) == 9, "policy: origin log classes");
        check(MediaPermissionPolicy.LogCode(1, Mic, www, MediaPermissionPolicy.Reason.SettingOff, CoreWebView2PermissionState.Deny, false) == 111022,
            "policy: numeric log code layout");
        check(WebViewService.PathCategory("https://www.instagram.com/call/?x=1") == 2 && WebViewService.PathCategory("https://www.instagram.com/direct/t/1/") == 1 &&
            WebViewService.PathCategory("https://www.instagram.com/") == 3 && WebViewService.PathCategory("nonsense") == 0, "policy: path log classes");
        var settings = new Models.AppSettings();
        check(!settings.AllowMicrophone && !settings.AllowCamera && settings.MediaPermissionRevision == 0, "defaults keep media off");
        var legacy = JsonSerializer.Deserialize<Models.AppSettings>("{\"AppNotifications\":true}")!;
        check(!legacy.AllowMicrophone && !legacy.AllowCamera && legacy.MediaPermissionRevision == 0, "legacy settings keep media off and need recovery");
    }

    internal static async Task RunServiceAsync(MainWindow window, SettingsService settings, Action<bool, string> check)
    {
        WebViewService.DiagnosticBrowserArguments = FakeDevices;
        WebViewService.BlockedNoticeInterval = TimeSpan.FromSeconds(2);
        var events = new List<(LogEvent Name, int? Code)>();
        var previousObserver = LoggingService.DiagnosticObserver;
        LoggingService.DiagnosticObserver = (name, error, code) => { previousObserver?.Invoke(name, error, code); events.Add((name, code)); };
        var host = new Grid();
        window.BrowserHost.Children.Add(host);
        settings.Current.AppNotifications = true;
        settings.Current.AllowMicrophone = false;
        settings.Current.AllowCamera = false;
        settings.Current.MediaPermissionRevision = 0;
        using var notifications = new NotificationService(window.Dispatcher, () => settings.Current.AppNotifications, _ => { }, new Presenter());
        var prompts = new List<string>();
        Func<IReadOnlyList<CoreWebView2PermissionKind>, Task<bool>> answer = _ => Task.FromResult(true);
        var blocked = new List<CoreWebView2PermissionKind>();
        var frames = new List<CoreWebView2Frame>();
        WebViewService Create()
        {
            var created = new WebViewService(host, window, settings, notifications);
            created.DiagnosticPermissionPrompt = (kinds, _) => { prompts.Add(string.Join("+", kinds)); return answer(kinds); };
            created.MediaBlocked += kind => blocked.Add(kind);
            created.DiagnosticCallConfigure = Serve; // offline: call pages never reach the network
            return created;
        }
        var service = Create();

        async Task StartAsync()
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await service.InitializeAsync(core =>
            {
                Serve(core);
                core.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
                core.FrameCreated += (_, e) => frames.Add(e.Frame);
            });
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            check(!service.NeedsRecovery && service.Core is not null, "service (re)starts on the diagnostic profile");
        }
        // An app restart: a new service after the previous browser process has
        // exited, so the profile is read back from disk rather than from memory.
        async Task RestartAsync()
        {
            int pid = (int)service.Core!.BrowserProcessId;
            service.Dispose();
            try { using var browser = System.Diagnostics.Process.GetProcessById(pid); await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (ArgumentException) { }
            service = Create();
            await StartAsync();
        }
        CoreWebView2 Web() => service.Core!;
        Task<string> Capture(bool audio, bool video) => CaptureAsync(Web(), audio, video);
        async Task<int> StateAsync(CoreWebView2PermissionKind kind) => await MediaPermissionPolicy.LogStateAsync(Web().Profile, kind);
        async Task ApplyAsync(bool? mic = null, bool? cam = null)
        {
            if (mic is bool m) settings.Current.AllowMicrophone = m;
            if (cam is bool c) settings.Current.AllowCamera = c;
            await service.ApplySettingsAsync();
        }
        async Task SetBothAsync(CoreWebView2PermissionKind kind, CoreWebView2PermissionState state)
        {
            foreach (string origin in MediaPermissionPolicy.Origins) await Web().Profile.SetPermissionStateAsync(kind, origin, state);
        }
        async Task<(string Result, int Prompts)> HeldCaptureAsync(Func<Task> whilePending, bool release = true)
        {
            var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            answer = _ => held.Task;
            int before = prompts.Count;
            var web = Web();
            await web.ExecuteScriptAsync("window.__g=null;window.__streams=window.__streams||[];navigator.mediaDevices.getUserMedia({audio:true}).then(s=>{__streams.push(s);__g='ok'},e=>__g='err:'+e.name);");
            await WaitUntilAsync(() => Task.FromResult(prompts.Count > before), 5000);
            int asked = prompts.Count - before;
            await whilePending();
            if (release) held.TrySetResult(true);
            await Task.Delay(500);
            answer = _ => Task.FromResult(true);
            string result = "gone";
            try { result = JsonSerializer.Deserialize<string?>(await web.ExecuteScriptAsync("window.__g").WaitAsync(TimeSpan.FromSeconds(2))) ?? "pending"; }
            catch (Exception) { }
            return (result, asked);
        }

        try
        {
            // 1. Default (switches off), fresh profile.
            await StartAsync();
            check(settings.Current.MediaPermissionRevision == 1, "startup records the one-time media recovery");
            check(await Capture(true, false) == "err:NotAllowedError" && prompts.Count == 0, "off: microphone denied without a dialog");
            await Task.Delay(200);
            check(blocked.SequenceEqual(new[] { Mic }), "off: one actionable blocked notice");
            check(await StateAsync(Mic) == 100, "off: app denial is not saved in the profile");
            check(events.Any(e => e.Name == LogEvent.MediaPermission && e.Code == 111022), "off: denial logged with fixed code");
            check(await Capture(true, false) == "err:NotAllowedError" && prompts.Count == 0 && blocked.Count == 1,
                "off: repeated request still reaches the app, notice throttled");
            check(await Capture(false, true) == "err:NotAllowedError" && await StateAsync(Cam) == 200, "off: camera denied and not saved");
            await Task.Delay(200);
            check(blocked.Contains(Cam), "off: camera notice is separate");

            // 2. false -> true: asks once, then remembered (www and root).
            await ApplyAsync(mic: true);
            check(await Capture(true, false) == "ok:audio/live" && prompts.SequenceEqual(new[] { "Microphone" }), "on: asks once and grants www");
            check(await StateAsync(Mic) == 101, "on: user Allow saved for www only");
            await StopTracksAsync(Web());
            check(await Capture(true, false) == "ok:audio/live" && prompts.Count == 1, "on: saved Allow needs no second dialog");
            await StopTracksAsync(Web());
            await NavigateAsync(Web(), Root);
            check(await Capture(true, false) == "ok:audio/live" && prompts.Count == 2 && await StateAsync(Mic) == 111, "on: root origin asked separately");

            // 3. true -> false with a live stream: stops, and new requests are denied.
            check(await TracksAsync(Web()) == "audio/live", "live root stream before switch off");
            await ApplyAsync(mic: false);
            check(await WaitUntilAsync(async () => await TracksAsync(Web()) == "audio/ended", 3000), "switch off ends the live microphone track");
            check(await StateAsync(Mic) == 100, "switch off clears saved grants on both origins");
            check(await Capture(true, false) == "err:NotAllowedError" && prompts.Count == 2, "switch off denies the next request without a dialog");
            await StopTracksAsync(Web());

            // 4. Historical denials from builds <= 1.1.0.
            await SetBothAsync(Mic, CoreWebView2PermissionState.Deny);
            await RestartAsync();
            check(await StateAsync(Mic) == 100, "restart while off removes a stale saved denial");
            settings.Current.AllowMicrophone = true;
            settings.Current.MediaPermissionRevision = 0;
            await SetBothAsync(Mic, CoreWebView2PermissionState.Deny);
            await RestartAsync();
            check(await StateAsync(Mic) == 100 && settings.Current.MediaPermissionRevision == 1, "one-time recovery clears a legacy denial while on");
            check(await Capture(true, false) == "ok:audio/live" && prompts.Count == 3, "recovered profile asks again and grants");
            await StopTracksAsync(Web());

            // 5. Explicit No, restart, re-enable and reset.
            check(await service.ResetPermissionsAsync() == PermissionResetResult.Reset && await StateAsync(Mic) == 100, "reset reports success and clears the grant");
            answer = _ => Task.FromResult(false);
            check(await Capture(true, false) == "err:NotAllowedError" && prompts.Count == 4 && await StateAsync(Mic) == 102, "No is saved as the site answer");
            check(await Capture(true, false) == "err:NotAllowedError" && prompts.Count == 4, "saved No does not ask again");
            answer = _ => Task.FromResult(true);
            await RestartAsync();
            int afterRestart = await StateAsync(Mic);
            check(afterRestart == 102, "restart keeps the user's saved No in the profile (state " + afterRestart + ")");
            check(await Capture(true, false) == "err:NotAllowedError" && prompts.Count == 4, "restart: saved No still denies without a dialog");
            await ApplyAsync(mic: false);
            await ApplyAsync(mic: true);
            check(await StateAsync(Mic) == 100, "turning the switch off and on clears the saved No");
            check(await Capture(true, false) == "ok:audio/live" && prompts.Count == 5, "re-enabled switch asks again");
            await StopTracksAsync(Web());
            await ApplyAsync(mic: false);
            await ApplyAsync(mic: true);
            answer = _ => Task.FromResult(false);
            await Capture(true, false);
            answer = _ => Task.FromResult(true);
            check(await StateAsync(Mic) == 102, "second No saved");
            check(await service.ResetPermissionsAsync() == PermissionResetResult.Reset && await StateAsync(Mic) == 100 &&
                await Web().ExecuteScriptAsync("Notification.permission") == "\"granted\"", "reset clears No and keeps notifications");
            check(await Capture(true, false) == "ok:audio/live" && prompts.Count == 7, "reset asks again");
            await StopTracksAsync(Web());

            // 6. Independent switches and combined requests.
            check(await Capture(true, true) == "err:NotAllowedError" && prompts.Count == 7, "camera off fails the combined request without a dialog");
            check(await StateAsync(Mic) == 101 && await StateAsync(Cam) == 200, "camera denial leaves the microphone grant intact");
            check(await Capture(true, false) == "ok:audio/live", "voice-only request still works with camera off");
            await StopTracksAsync(Web());
            await ApplyAsync(cam: true);
            await service.ResetPermissionsAsync();
            check(await Capture(true, true) == "ok:audio/live,video/live" && prompts.Count == 8 && prompts[7] is "Microphone+Camera" or "Camera+Microphone",
                "combined request asks one question for both devices");
            check(await StateAsync(Mic) == 101 && await StateAsync(Cam) == 201, "combined answer saved per device");
            await StopTracksAsync(Web());
            await Task.Delay(2100);
            blocked.Clear();
            await ApplyAsync(cam: false);
            await StopTracksAsync(Web());
            check(await Capture(false, true) == "err:NotAllowedError" && await StateAsync(Mic) == 101 && await StateAsync(Cam) == 200,
                "camera switch off revokes only the camera");
            await ApplyAsync(cam: true);

            // 7. Lifecycle while a question is open.
            await service.ResetPermissionsAsync();
            var (offResult, offAsked) = await HeldCaptureAsync(() => ApplyAsync(mic: false));
            check(offAsked == 1 && offResult == "err:NotAllowedError" && await StateAsync(Mic) == 100 && service.PendingPermissionCount == 0,
                "switch off while asking denies the request and ignores the late Yes");
            await ApplyAsync(mic: true);
            var (navResult, _) = await HeldCaptureAsync(() => NavigateAsync(Web(), Www));
            check(await StateAsync(Mic) == 100 && service.PendingPermissionCount == 0 && navResult != "ok",
                "navigation while asking cannot grant the old document");
            var oldCore = Web();
            var (_, restartAsked) = await HeldCaptureAsync(StartAsync);
            int rebuiltState = await StateAsync(Mic);
            check(restartAsked == 1 && service.PendingPermissionCount == 0 && Web() != oldCore,
                "WebView rebuild while asking completes the old request (asked " + restartAsked + ", pending " + service.PendingPermissionCount + ")");
            // An in-place rebuild may start a browser process that reads profile
            // prefs not yet flushed by the previous one; only a grant matters here.
            check(rebuiltState % 10 != 1 && rebuiltState / 10 % 10 != 1, "WebView rebuild while asking leaves no grant (state " + rebuiltState + ")");
            await service.ResetPermissionsAsync();
            var (_, crashAsked) = await HeldCaptureAsync(async () =>
            {
                try { await Web().CallDevToolsProtocolMethodAsync("Page.crash", "{}").WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { }
                await WaitUntilAsync(() => Task.FromResult(service.NeedsRecovery), 5000);
            });
            check(crashAsked == 1 && service.NeedsRecovery && service.PendingPermissionCount == 0, "renderer crash while asking completes the request");
            await StartAsync();
            int crashState = await StateAsync(Mic);
            check(crashState % 10 != 1 && crashState / 10 % 10 != 1, "no grant survives the crash (state " + crashState + ")");
            check(await service.ResetPermissionsAsync() == PermissionResetResult.Reset, "reset after recovery");
            int beforeDuplicate = prompts.Count;
            await Web().ExecuteScriptAsync("window.__d=[];for(let i=0;i<2;i++)navigator.mediaDevices.getUserMedia({audio:true}).then(s=>{__d.push('ok');s.getTracks().forEach(t=>t.stop())},e=>__d.push(e.name));");
            check(await WaitUntilAsync(async () => await Web().ExecuteScriptAsync("window.__d.length") == "2", 8000) &&
                await Web().ExecuteScriptAsync("window.__d.join()") == "\"ok,ok\"" && prompts.Count - beforeDuplicate == 1,
                "duplicate simultaneous requests share one question");

            // 8. Origins at runtime: an untrusted Instagram subdomain and a cross-origin iframe.
            blocked.Clear();
            await NavigateAsync(Web(), "https://help.instagram.com/");
            check(await Capture(true, false) == "err:NotAllowedError" && prompts.Count == beforeDuplicate + 1 && blocked.Count == 0,
                "unapproved Instagram subdomain is denied without a dialog");
            check(MediaPermissionPolicy.StateOf(await Web().Profile.GetNonDefaultPermissionSettingsAsync(), Mic, "https://help.instagram.com") ==
                CoreWebView2PermissionState.Default, "subdomain denial is not saved");
            // Cross-origin iframes: Chromium attributes a delegated request to the
            // top-level origin, so the media policy is applied to that origin.
            await NavigateAsync(Web(), Www);
            await service.ResetPermissionsAsync();
            async Task<CoreWebView2Frame?> FrameAsync(string allow)
            {
                frames.Clear();
                await Web().ExecuteScriptAsync("document.body.innerHTML='<iframe src=\"https://www.facebook.com/frame\"" + allow + "></iframe>';");
                await WaitUntilAsync(() => Task.FromResult(frames.Count > 0), 5000);
                await Task.Delay(800);
                return frames.FirstOrDefault();
            }
            int framePrompts = prompts.Count;
            var undelegated = await FrameAsync("");
            string undelegatedResult = undelegated is null ? "no-frame" : await CaptureWithAsync(undelegated.ExecuteScriptAsync, true, false);
            check(undelegatedResult == "err:NotAllowedError" && prompts.Count == framePrompts,
                "undelegated cross-origin iframe cannot ask or capture (" + undelegatedResult + ")");
            events.Clear();
            var delegated = await FrameAsync(" allow=\"microphone\"");
            string delegatedResult = delegated is null ? "no-frame" : await CaptureWithAsync(delegated.ExecuteScriptAsync, true, false);
            var frameEvent = events.FirstOrDefault(e => e.Name == LogEvent.MediaPermission && e.Code is int c && c / 10 % 100 == (int)MediaPermissionPolicy.Reason.Prompt);
            int frameOrigin = frameEvent.Code is int fc ? fc / 1000 % 10 : 0;
            check(frameOrigin == 1 && delegatedResult == "ok:audio/live" && prompts.Count == framePrompts + 1 && await StateAsync(Mic) == 101,
                "delegated iframe request is attributed to www.instagram.com and follows its policy (origin class " + frameOrigin + ", " + delegatedResult + ")");
            await ApplyAsync(mic: false);
            check(await CaptureWithAsync(delegated!.ExecuteScriptAsync, true, false) == "err:NotAllowedError" && prompts.Count == framePrompts + 1,
                "delegated iframe is denied when the switch is off");
            await ApplyAsync(mic: true);

            // 9. Hidden inbox monitor on the shared profile, with and without a saved grant.
            await NavigateAsync(Web(), Www);
            check(await Capture(true, false) == "ok:audio/live" && await StateAsync(Mic) == 101, "primary holds a saved microphone grant");
            await StopTracksAsync(Web());
            foreach (bool withGrant in new[] { true, false })
            {
                if (!withGrant) await SetBothAsync(Mic, CoreWebView2PermissionState.Default);
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var monitor = new DirectInboxMonitor(window.Dispatcher, notifications, () => true);
                var monitorPrompts = prompts.Count;
                await monitor.StartAsync(Web(), new WindowInteropHelper(window).Handle, core =>
                {
                    Serve(core);
                    core.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
                });
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var hidden = monitor.Core!;
                string label = withGrant ? " (saved grant)" : " (no grant)";
                check(await hidden.ExecuteScriptAsync("typeof navigator.mediaDevices + ',' + typeof navigator.webkitGetUserMedia + ',' + typeof navigator.getUserMedia") ==
                    "\"undefined,undefined,undefined\"", "monitor has no capture entry points" + label);
                string frameAccess = await hidden.ExecuteScriptAsync("(()=>{const f=document.createElement('iframe');document.body.appendChild(f);" +
                    "const m=f.contentWindow.navigator.mediaDevices;if(!m)return 'blocked';window.__fg=null;m.getUserMedia({audio:true}).then(s=>{__fg='captured';s.getTracks().forEach(t=>t.stop())},e=>__fg='err:'+e.name);return 'exposed'})()");
                if (frameAccess == "\"exposed\"")
                {
                    string fg = "null";
                    await WaitUntilAsync(async () => (fg = await hidden.ExecuteScriptAsync("window.__fg")) != "null", 5000);
                    check(fg != "\"captured\"", "monitor same-origin blank iframe cannot capture" + label);
                }
                else check(frameAccess == "\"blocked\"", "monitor blank iframe has no capture entry point" + label);
                check(prompts.Count == monitorPrompts && await StateAsync(Mic) == (withGrant ? 101 : 100), "monitor never asks nor changes the profile" + label);
                check(await hidden.ExecuteScriptAsync("Notification.permission") == "\"granted\"", "monitor keeps notification permission" + label);
                monitor.Dispose();
            }

            // 10. Background policy with a live stream (Low never suspends or ends capture).
            await NavigateAsync(Web(), Www);
            check(await Capture(true, true) == "ok:audio/live,video/live", "live call-like stream before backgrounding");
            settings.Current.AppNotifications = false;
            settings.Current.BackgroundLowMemory = true;
            await service.ApplySettingsAsync();
            service.SetBackground(true);
            await Task.Delay(2500);
            check((!service.MemoryApiAvailable || Web().MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low) && !Web().IsSuspended &&
                await TracksAsync(Web()) == "audio/live,video/live", "tray with notifications off: low memory keeps capture live");
            service.SetBackground(false);
            settings.Current.AppNotifications = true;
            await service.ApplySettingsAsync();
            service.SetBackground(true);
            await Task.Delay(1000);
            check((!service.MemoryApiAvailable || Web().MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal) &&
                await TracksAsync(Web()) == "audio/live,video/live", "tray with notifications on: normal memory keeps capture live");
            service.SetBackground(false);
            await StopTracksAsync(Web());

            // 13. Instagram call pop-up: its own window with real opener semantics.
            CallWindow.DiagnosticHidden = true;
            await NavigateAsync(Web(), Www);
            await ApplyAsync(mic: true);
            string primarySource = Web().Source;
            bool mainCloseRequested = false;
            service.CloseRequested += () => mainCloseRequested = true;
            async Task<CoreWebView2> OpenCallAsync()
            {
                int before = service.CallWindows.Count;
                await Web().ExecuteScriptAsync("window.__call=window.open('https://www.instagram.com/call/?fixture=1','call','width=820,height=600');");
                await WaitUntilAsync(() => Task.FromResult(service.CallWindows.Count > before), 10000);
                check(service.CallWindows.Count == before + 1, "call pop-up opens a separate call window");
                var created = service.CallWindows[^1].View.CoreWebView2;
                await WaitUntilAsync(async () => created.Source.StartsWith("https://www.instagram.com/call/", StringComparison.Ordinal) &&
                    await created.ExecuteScriptAsync("document.readyState") == "\"complete\"", 10000);
                return created;
            }
            var callCore = await OpenCallAsync();
            check(Web().Source == primarySource, "main view stays on its own page during a call");
            check(callCore.Source.StartsWith("https://www.instagram.com/call/", StringComparison.Ordinal),
                "call window loads the call page itself (" + MediaPermissionPolicy.OriginCategory(callCore.Source) + "/" + WebViewService.PathCategory(callCore.Source) + ")");
            check(await callCore.ExecuteScriptAsync("window.opener !== null") == "true" &&
                await Web().ExecuteScriptAsync("window.__call !== null && !window.__call.closed") == "true", "call window keeps window.opener and the opener handle");
            check(await callCore.ExecuteScriptAsync("typeof navigator.mediaDevices") == "\"object\"", "call window is not capture-blocked like the hidden monitor");
            await service.ResetPermissionsAsync();
            int callPrompts = prompts.Count;
            check(await CaptureAsync(callCore, true, true) == "ok:audio/live,video/live" && prompts.Count == callPrompts + 1,
                "call window asks through the same permission flow and captures");
            settings.Current.AppNotifications = false;
            settings.Current.BackgroundLowMemory = true;
            await service.ApplySettingsAsync();
            service.SetBackground(true);
            check(!service.MemoryApiAvailable || Web().MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal,
                "main page stays at normal memory while a call window is open");
            service.SetBackground(false);
            settings.Current.AppNotifications = true;
            await service.ApplySettingsAsync();
            await ApplyAsync(cam: false);
            // Runtime fact: revoking one device ends every track of that
            // getUserMedia request; the other device's grant is kept.
            check(await WaitUntilAsync(async () => (await TracksAsync(callCore)).EndsWith("video/ended", StringComparison.Ordinal), 3000),
                "camera switch off stops the camera in the call window (" + await TracksAsync(callCore) + ")");
            await StopTracksAsync(callCore);
            int afterCameraOff = prompts.Count;
            check(await CaptureAsync(callCore, true, false) == "ok:audio/live" && prompts.Count == afterCameraOff && await StateAsync(Mic) == 101,
                "microphone grant survives the camera switch-off; voice resumes without a dialog");
            await ApplyAsync(mic: false);
            check(await WaitUntilAsync(async () => await TracksAsync(callCore) == "audio/ended", 3000), "microphone switch off stops the call window microphone");
            await ApplyAsync(mic: true, cam: true);
            await callCore.ExecuteScriptAsync("window.close();");
            check(await WaitUntilAsync(() => Task.FromResult(service.CallWindows.Count == 0), 5000) && !mainCloseRequested && service.Core is not null,
                "call page closing itself closes only the call window");
            check(await WaitUntilAsync(async () => await Web().ExecuteScriptAsync("window.__call.closed") == "true", 3000), "opener sees the call window closed");
            // Closing the call window while its permission question is open.
            callCore = await OpenCallAsync();
            await service.ResetPermissionsAsync();
            var heldCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            answer = _ => heldCall.Task;
            int beforeHeld = prompts.Count;
            await callCore.ExecuteScriptAsync("navigator.mediaDevices.getUserMedia({audio:true}).then(()=>{},()=>{});");
            await WaitUntilAsync(() => Task.FromResult(prompts.Count > beforeHeld), 5000);
            service.CallWindows[0].Close();
            heldCall.TrySetResult(true);
            await Task.Delay(500);
            answer = _ => Task.FromResult(true);
            check(prompts.Count == beforeHeld + 1 && service.CallWindows.Count == 0 && service.PendingPermissionCount == 0 && await StateAsync(Mic) == 100,
                "closing the call window while asking completes the request without a grant");
            // Other pop-ups keep their previous behavior.
            var external = new List<Uri>();
            ShellService.DiagnosticExternalOpened = external.Add;
            await Web().ExecuteScriptAsync("window.open('https://evil.test/call/','_blank');window.open('https://help.instagram.com/call/','_blank');");
            await Task.Delay(1500);
            check(service.CallWindows.Count == 0 && external.Any(u => u.Host == "evil.test"), "untrusted call-like pop-ups never get a call window");
            await NavigateAsync(Web(), Www);
            await Web().ExecuteScriptAsync("window.open('https://www.instagram.com/p/fixture/','_blank');");
            await WaitUntilAsync(() => Task.FromResult(Web().Source.Contains("/p/fixture/", StringComparison.Ordinal)), 5000);
            ShellService.DiagnosticExternalOpened = null;
            check(service.CallWindows.Count == 0 && Web().Source.Contains("/p/fixture/", StringComparison.Ordinal), "other Instagram pop-ups still open in the main view");
            // App shutdown closes an open call window.
            await NavigateAsync(Web(), Www);
            await OpenCallAsync();
            var lastCall = service.CallWindows[0];
            service.Dispose();
            check(service.CallWindows.Count == 0 && !lastCall.IsVisible, "disposing the service closes call windows");

            // 11. Reset before initialization reports it instead of claiming success.
            using var idle = new NotificationService(window.Dispatcher, () => false, _ => { }, new Presenter());
            var uninitialized = new WebViewService(new Grid(), window, settings, idle);
            check(await uninitialized.ResetPermissionsAsync() == PermissionResetResult.NotReady, "reset without a WebView reports NotReady");
            uninitialized.Dispose();

            // 12. Windows privacy entry points are fixed Settings pages.
            var opened = new List<string>();
            ShellService.DiagnosticSettingsOpened = opened.Add;
            ShellService.OpenPrivacySettings(camera: false);
            ShellService.OpenPrivacySettings(camera: true);
            ShellService.DiagnosticSettingsOpened = null;
            check(opened.SequenceEqual(new[] { "ms-settings:privacy-microphone", "ms-settings:privacy-webcam" }), "privacy shortcuts open fixed Windows pages");
        }
        finally
        {
            LoggingService.DiagnosticObserver = previousObserver;
            service.Dispose();
            window.BrowserHost.Children.Remove(host);
            WebViewService.DiagnosticBrowserArguments = null;
            CallWindow.DiagnosticHidden = false;
        }
    }

    public static async Task RunAsync(MainWindow window, SettingsService settings, string output)
    {
        var checks = new Dictionary<string, bool>();
        Dictionary<string, string>? facts = null;
        string? failure = null;
        void Check(bool condition, string name)
        {
            checks[name] = condition;
            if (ProgressPath is { } path) try { File.WriteAllText(path, JsonSerializer.Serialize(checks)); } catch (IOException) { }
            if (!condition) throw new InvalidOperationException(name);
        }
        try
        {
            ProgressPath = output + ".progress.json";
            RunPolicy(Check);
            if (!Environment.GetCommandLineArgs().Contains("--media-core-only"))
            {
                facts = await ProbeRuntimeAsync(window);
                await RunServiceAsync(window, settings, Check);
            }
        }
        catch (Exception error)
        {
            failure = error is InvalidOperationException ? checks.LastOrDefault(x => !x.Value).Key ?? error.GetType().Name : error.GetType().Name;
            LoggingService.Write(LogEvent.UnexpectedException, error);
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = failure is null,
                scope = "media-permission", failure, facts, checks }, new JsonSerializerOptions { WriteIndented = true }));
            window.PrepareForShutdown(); window.DisposeResources();
            Application.Current.Shutdown(failure is null ? 0 : 1);
        }
    }
}
