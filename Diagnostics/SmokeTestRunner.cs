using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using InstaDesktop.Models;
using InstaDesktop.Services;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop.Diagnostics;

// Explicit --smoke-test mode only. All profile files and test markers are isolated.
// It never signs in or accesses passwords, cookies, tokens or Instagram messages.
internal static class SmokeTestRunner
{
    public static async Task RunAsync(MainWindow window, SettingsService settings, string output)
    {
        var checks = new Dictionary<string, object>();
        int exitCode = 1;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            Check(NavigationPolicy.IsTrusted("https://www.instagram.com/"), "official origin accepted", checks);
            Check(NavigationPolicy.IsTrusted("https://instagram.com/direct/"), "root domain accepted", checks);
            Check(!NavigationPolicy.IsTrusted("https://instagram.com.evil.example/"), "suffix attack blocked", checks);
            Check(!NavigationPolicy.IsTrusted("https://evilinstagram.com/"), "lookalike blocked", checks);
            Check(!NavigationPolicy.IsTrusted("http://instagram.com/"), "insecure injection blocked", checks);
            Check(!NavigationPolicy.IsTrusted("https://user@instagram.com/"), "userinfo URL blocked", checks);
            Check(!NavigationPolicy.IsTrusted("https://instagram.com:8443/"), "nonstandard port blocked", checks);
            Check(!NavigationPolicy.TryExternal("file:///C:/Windows/system32/cmd.exe", out _), "file launch blocked", checks);
            Check(!NavigationPolicy.TryExternal("javascript:alert(1)", out _), "script launch blocked", checks);
            Check(NavigationPolicy.UpgradeInstagramHttp("http://instagram.com/direct/") == "https://instagram.com/direct/", "HTTPS upgrade", checks);

            var changed = settings.Current.Copy();
            changed.Width = 1120; changed.Height = 740; changed.DeveloperTools = true;
            changed.SidebarMode = SidebarMode.Compact;
            changed.CompactInstagramLayout = false;
            await settings.SaveAsync(changed);
            var reloaded = new SettingsService();
            await reloaded.LoadAsync();
            Check(reloaded.Current.Width == 1120 && reloaded.Current.DeveloperTools, "settings roundtrip", checks);
            Check(reloaded.Current.SidebarMode == SidebarMode.Compact && !reloaded.Current.CompactInstagramLayout && window.IsSidebarCompact,
                "desktop settings roundtrip and immediate sidebar update", checks);
            changed.DeveloperTools = false;
            changed.SidebarMode = SidebarMode.Expanded;
            changed.CompactInstagramLayout = true;
            await settings.SaveAsync(changed);

            var firstLoad = WaitForLoadAsync(window.Web);
            await window.Web.InitializeAsync();
            if (window.Web.LastFailure is { } failure) throw new InvalidOperationException(failure);
            await firstLoad;
            var core = window.Web.Core ?? throw new InvalidOperationException("Core was not initialized");
            checks["runtime"] = core.Environment.BrowserVersionString;
            Check(NavigationPolicy.IsTrusted(core.Source), "official Instagram navigation", checks);
            Check(!core.Settings.AreDevToolsEnabled && !core.Settings.IsStatusBarEnabled &&
                !core.Settings.IsZoomControlEnabled, "browser controls disabled", checks);
            Check(await core.ExecuteScriptAsync("Boolean(document.querySelector('body') && document.body.childElementCount > 0)") == "true",
                "Instagram document rendered", checks);
            Check(await core.ExecuteScriptAsync("Boolean(window.__InstaDesktopCustomization?.version === 1 && document.getElementById('instadesktop-custom-style'))") == "true",
                "CSS and JavaScript injection", checks);

            await core.ExecuteScriptAsync("document.getElementById('instadesktop-custom-style').remove();");
            await Task.Delay(600); // One-shot diagnostic deadline for the debounced observer.
            Check(await core.ExecuteScriptAsync("Boolean(document.getElementById('instadesktop-custom-style'))") == "true", "style restored after DOM replacement", checks);
            await core.ExecuteScriptAsync("history.pushState(null, '', '#instadesktop-test');");
            await Task.Delay(350);
            Check(await core.ExecuteScriptAsync("Boolean(document.getElementById('instadesktop-custom-style'))") == "true", "injection survives SPA navigation", checks);

            foreach (var section in new[] { NavigationSection.Home, NavigationSection.Messages, NavigationSection.Reels, NavigationSection.Explore })
            {
                await core.ExecuteScriptAsync("history.pushState(null, '', " + JsonSerializer.Serialize(InstagramRoutes.PathFor(section)) + ");");
                await Task.Delay(100);
                Check(window.SelectedSection == section, "SPA sidebar route " + section, checks);
            }
            await core.ExecuteScriptAsync("history.replaceState(null, '', '/diagnostic_profile/');");
            await Task.Delay(100);
            Check(window.SelectedSection == NavigationSection.Profile, "replaceState profile route", checks);
            Check(core.CanGoBack, "history back enabled", checks);
            core.GoBack();
            await Task.Delay(250);
            Check(core.CanGoForward, "history forward enabled", checks);
            core.GoForward();
            await Task.Delay(250);
            await core.ExecuteScriptAsync("history.replaceState(null, '', '/');");

            // Synthetic DOM in the signed-out diagnostic profile tests selectors without account actions.
            Check(await core.ExecuteScriptAsync("""
                (() => {
                    const fixture = document.createElement('section');
                    fixture.id = 'instadesktop-diagnostic-fixture';
                    fixture.innerHTML = '<nav><a href="/">Home</a><a href="/explore/">Explore</a><a href="/reels/">Reels</a><button aria-label="Search"><svg aria-label="Search"></svg></button></nav><main><nav>Conversation navigation</nav><article><button>Like</button></article><div role="dialog">Share</div></main>';
                    document.body.appendChild(fixture);
                    fixture.querySelector('button').onclick = () => fixture.dataset.clicked = 'true';
                    const ui = window.__InstaDesktopCustomization;
                    ui.applyInstagramTweaks(fixture);
                    return getComputedStyle(fixture.querySelector('nav')).display === 'none' &&
                        getComputedStyle(fixture.querySelector('main nav')).display !== 'none' &&
                        getComputedStyle(fixture.querySelector('[role="dialog"]')).display !== 'none';
                })()
                """) == "true", "only global navigation hidden; content and dialogs retained", checks);
            Check(await core.ExecuteScriptAsync("Boolean(window.__InstaDesktopCustomization.invokeAction('search') && document.getElementById('instadesktop-diagnostic-fixture').dataset.clicked === 'true')") == "true",
                "semantic search action invokes official-style control", checks);
            Check(await core.ExecuteScriptAsync("Boolean(document.documentElement.hasAttribute('data-instadesktop-show-nav'))") == "true", "panel navigation fallback visible", checks);
            Check(await core.ExecuteScriptAsync("window.__InstaDesktopCustomization.invokeAction('unknown')") == "false", "unknown action fails safely", checks);
            await core.ExecuteScriptAsync("document.getElementById('instadesktop-diagnostic-fixture').remove(); window.__InstaDesktopCustomization.revealNavigation(false);");

            var appearance = settings.Current.Copy();
            appearance.CompactInstagramLayout = false;
            appearance.DeveloperTools = true;
            await settings.SaveAsync(appearance);
            await window.Web.ApplySettingsAsync(false);
            Check(await core.ExecuteScriptAsync("document.documentElement.hasAttribute('data-instadesktop-compact')") == "false" && core.Settings.AreDevToolsEnabled,
                "compact layout and devtools apply without reload", checks);
            appearance.CompactInstagramLayout = true;
            appearance.DeveloperTools = false;
            await settings.SaveAsync(appearance);
            await window.Web.ApplySettingsAsync(false);
            await window.Web.ReloadCustomizationAsync();
            Check(await core.ExecuteScriptAsync("document.documentElement.hasAttribute('data-instadesktop-compact')") == "true", "live customization reload", checks);
            window.Width = 900; window.UpdateLayout();
            Check(window.IsSidebarCompact, "narrow window auto compacts sidebar", checks);
            window.Width = 1200; window.UpdateLayout();
            Check(!window.IsSidebarCompact && window.Web.View!.ZoomFactor == 1, "wide window restores sidebar at 100 percent zoom", checks);
            window.SetMediaFullscreen(true);
            Check(window.Sidebar.Visibility == Visibility.Collapsed && window.Toolbar.Visibility == Visibility.Collapsed, "fullscreen hides native shell", checks);
            window.SetMediaFullscreen(false);
            Check(window.Sidebar.Visibility == Visibility.Visible && window.Toolbar.Visibility == Visibility.Visible, "fullscreen restores native shell", checks);

            var external = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            ShellService.DiagnosticExternalOpened = uri => external.TrySetResult(uri);
            core.Navigate("https://example.com/instadesktop-navigation-test");
            var externalUri = await external.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(externalUri.Host == "example.com" && NavigationPolicy.IsTrusted(core.Source), "external navigation delegated to Windows browser", checks);
            var popup = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            ShellService.DiagnosticExternalOpened = uri => popup.TrySetResult(uri);
            await core.ExecuteScriptAsync("window.open('https://example.com/instadesktop-popup-test', '_blank');");
            Check((await popup.Task.WaitAsync(TimeSpan.FromSeconds(10))).Host == "example.com", "external popup delegated", checks);
            ShellService.DiagnosticExternalOpened = null;

            // Only the signed-out diagnostic page is captured, never the user's profile.
            await using (var capture = File.Create(Path.ChangeExtension(output, ".png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);

            window.WindowState = WindowState.Minimized;
            Check(!core.IsSuspended, "minimized without suspension", checks);
            if (window.Web.MemoryApiAvailable)
                Check(core.MemoryUsageTargetLevel == (settings.Current.AppNotifications ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low), "minimize respects notification memory policy", checks);
            window.ShowFromTray();
            if (window.Web.MemoryApiAvailable)
                Check(core.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "restore sets normal memory", checks);
            window.HideToTray(showHint: false);
            Check(!window.IsVisible && !core.IsSuspended, "tray keeps scripts active", checks);
            Check(await core.ExecuteScriptAsync("1 + 1") == "2", "JavaScript executes while hidden", checks);
            window.ShowFromTray();

            string markerScript = "localStorage.setItem('instadesktop-diagnostic-marker', 'profile-roundtrip');";
            await core.ExecuteScriptAsync(markerScript);
            var reload = WaitForLoadAsync(window.Web);
            await window.Web.ReloadAsync();
            await reload;
            Check(await core.ExecuteScriptAsync("Boolean(document.getElementById('instadesktop-custom-style'))") == "true", "injection after reload", checks);
            Check(await core.ExecuteScriptAsync("localStorage.getItem('instadesktop-diagnostic-marker')") == "\"profile-roundtrip\"", "profile storage survives reload", checks);

            // Recreate the controller exactly as renderer-crash recovery does.
            var recreate = WaitForLoadAsync(window.Web);
            await window.Web.InitializeAsync();
            await recreate;
            core = window.Web.Core!;
            Check(await core.ExecuteScriptAsync("localStorage.getItem('instadesktop-diagnostic-marker')") == "\"profile-roundtrip\"", "profile storage survives controller recreation", checks);
            await core.ExecuteScriptAsync("localStorage.removeItem('instadesktop-diagnostic-marker');");

            // Kill only this diagnostic profile's own WebView browser process.
            var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCrash(string message, bool failed) { if (failed) crashed.TrySetResult(); }
            window.Web.StatusChanged += OnCrash;
            using (var browserProcess = Process.GetProcessById((int)core.BrowserProcessId)) browserProcess.Kill();
            await crashed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            window.Web.StatusChanged -= OnCrash;
            Check(window.Web.NeedsRecovery, "browser crash shows recovery UI", checks);
            var recovered = WaitForLoadAsync(window.Web);
            await window.Web.ReloadAsync();
            await recovered;
            core = window.Web.Core!;
            Check(NavigationPolicy.IsTrusted(core.Source) && !window.Web.NeedsRecovery, "reload recovers crashed browser", checks);

            var off = settings.Current.Copy();
            off.UiCustomization = false;
            await settings.SaveAsync(off);
            await window.Web.ApplySettingsAsync(customizationChanged: true);
            Check(await core.ExecuteScriptAsync("Boolean(window.__InstaDesktopCustomization)") == "false", "customization off", checks);
            Check(await core.ExecuteScriptAsync("document.querySelector('[data-instadesktop-nav], [data-instadesktop-main]') === null") == "true", "customization off removes layout markers", checks);

            // Render settings without changing the user's startup registration.
            var settingsWindow = new SettingsWindow(settings, window.Web) { Owner = window, ShowInTaskbar = false, Opacity = 0 };
            settingsWindow.Show();
            settingsWindow.UpdateLayout();
            Check(settingsWindow.IsLoaded, "settings window opens", checks);
            settingsWindow.Close();
            checks["appWorkingSetMB"] = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576d, 1);
            checks["note"] = "Host process only; not a total WebView2 memory benchmark. No signed-in workflows were tested.";
            exitCode = 0;
        }
        catch (Exception error)
        {
            checks["failure"] = error.GetType().Name;
            checks["failureMethod"] = error.TargetSite?.Name ?? "unknown";
            // Test-generated assertion labels only; never log exception messages.
            LoggingService.Write(LogEvent.UnexpectedException, error);
        }
        finally
        {
            ShellService.DiagnosticExternalOpened = null;
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                success = exitCode == 0, timestamp = DateTimeOffset.UtcNow,
                executable = Environment.ProcessPath, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            window.PrepareForShutdown();
            window.DisposeResources();
            Application.Current.Shutdown(exitCode);
        }
    }

    private static void Check(bool condition, string name, Dictionary<string, object> checks)
    {
        checks[name] = condition;
        if (!condition) throw new InvalidOperationException(name);
    }

    private static async Task WaitForLoadAsync(WebViewService web)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady() => completion.TrySetResult();
        void OnStatus(string message, bool failed) { if (failed) completion.TrySetException(new InvalidOperationException()); }
        web.Ready += OnReady;
        web.StatusChanged += OnStatus;
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { web.Ready -= OnReady; web.StatusChanged -= OnStatus; }
    }
}
