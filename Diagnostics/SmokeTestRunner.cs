using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using InstaDesktop.Localization;
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
            changed.Width = 1120; changed.Height = 740; changed.DeveloperTools = true; changed.ZoomFactor = 1.25;
            await settings.SaveAsync(changed);
            var reloaded = new SettingsService();
            await reloaded.LoadAsync();
            Check(reloaded.Current.Width == 1120 && reloaded.Current.DeveloperTools && reloaded.Current.ZoomFactor == 1.25, "settings roundtrip", checks);
            await settings.UpdateAsync(s => s.Left = 40);
            Check(settings.Current.Left == 40 && settings.Current.ZoomFactor == 1.25 && settings.Current.DeveloperTools,
                "partial settings update keeps other fields", checks);
            changed = settings.Current.Copy();
            changed.DeveloperTools = false; changed.ZoomFactor = 9; changed.Left = null;
            await settings.SaveAsync(changed);
            Check(settings.Current.ZoomFactor == 5, "zoom setting is clamped", checks);
            await settings.UpdateAsync(s => s.ZoomFactor = 1);

            var firstLoad = WaitForLoadAsync(window.Web);
            await window.Web.InitializeAsync();
            if (window.Web.LastFailure is { } failure) throw new InvalidOperationException(failure);
            await firstLoad;
            var core = window.Web.Core ?? throw new InvalidOperationException("Core was not initialized");
            checks["runtime"] = core.Environment.BrowserVersionString;
            Check(NavigationPolicy.IsTrusted(core.Source), "official Instagram navigation", checks);
            Check(!core.Settings.AreDevToolsEnabled && !core.Settings.IsStatusBarEnabled && core.Settings.IsZoomControlEnabled,
                "browser controls: no devtools or status bar, zoom available", checks);
            Check(await core.ExecuteScriptAsync("Boolean(document.querySelector('body') && document.body.childElementCount > 0)") == "true",
                "Instagram document rendered", checks);
            // An Emoji 13 character Windows 10's Segoe UI Emoji cannot draw.
            await core.ExecuteScriptAsync("document.fonts.load('16px \"-apple-system\"', '\\u{1F972}')" +
                ".then(f => window.__instaDesktopEmojiCheck = f.length > 0 && f.every(x => x.status === 'loaded'))");
            for (int i = 0; i < 50 && await core.ExecuteScriptAsync("window.__instaDesktopEmojiCheck") == "null"; i++) await Task.Delay(100);
            Check(await core.ExecuteScriptAsync("window.__instaDesktopEmojiCheck") == "true", "bundled Emoji font loaded", checks);
            Check(await core.ExecuteScriptAsync("Boolean(document.getElementById('instadesktop-viewport-style'))") == "true",
                "dark viewport style applied at document creation", checks);
            Check(await core.ExecuteScriptAsync("Boolean(window.__InstaDesktopCustomization || document.getElementById('instadesktop-custom-style'))") == "false",
                "original Instagram page without customization", checks);

            foreach (var section in new[] { NavigationSection.Home, NavigationSection.Messages, NavigationSection.Reels, NavigationSection.Explore })
            {
                await core.ExecuteScriptAsync("history.pushState(null, '', " + JsonSerializer.Serialize(InstagramRoutes.PathFor(section)) + ");");
                await Task.Delay(100);
                Check(window.Web.CurrentSection == section, "SPA route " + section, checks);
            }
            await core.ExecuteScriptAsync("history.replaceState(null, '', '/diagnostic_profile/');");
            await Task.Delay(100);
            Check(window.Web.CurrentSection == NavigationSection.Profile, "replaceState profile route", checks);
            Check(core.CanGoBack, "history back enabled", checks);
            core.GoBack();
            await Task.Delay(250);
            Check(core.CanGoForward, "history forward enabled", checks);
            core.GoForward();
            await Task.Delay(250);
            Check(!window.Web.NeedsRecovery, "fast back/forward is not reported as a failure", checks);
            await core.ExecuteScriptAsync("history.replaceState(null, '', '/');");

            // Ctrl+1..4 shortcut target: a real navigation to the section's page.
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
            { if (e.Uri.Contains("/explore", StringComparison.Ordinal)) started.TrySetResult(e.Uri); }
            core.NavigationStarting += OnStarting;
            var sectionLoad = WaitForLoadAsync(window.Web);
            await window.Web.NavigateSectionAsync(NavigationSection.Explore);
            string sectionUri = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            core.NavigationStarting -= OnStarting;
            await sectionLoad;
            Check(sectionUri == "https://www.instagram.com/explore/", "section shortcut navigates the page", checks);

            window.Web.StepZoom(+1);
            await Task.Delay(300);
            Check(Math.Abs(window.Web.ZoomFactor - 1.1) < 0.001 && window.ZoomButton.Visibility == Visibility.Visible &&
                (string)window.ZoomText.Text == "110%", "zoom in shows the zoom level", checks);
            window.Web.StepZoom(-1); window.Web.StepZoom(-1);
            await Task.Delay(300);
            Check(Math.Abs(window.Web.ZoomFactor - 0.9) < 0.001, "zoom out", checks);
            for (int i = 0; i < 30 && Math.Abs(settings.Current.ZoomFactor - 0.9) > 0.001; i++) await Task.Delay(100);
            Check(Math.Abs(settings.Current.ZoomFactor - 0.9) < 0.001, "zoom level is saved", checks);
            window.Web.ResetZoom();
            await Task.Delay(300);
            Check(window.Web.ZoomFactor == 1 && window.ZoomButton.Visibility == Visibility.Collapsed, "zoom reset hides the indicator", checks);
            // A page busy for a few seconds is not replaced when it answers in time.
            WebViewService.UnresponsiveGrace = TimeSpan.FromSeconds(1);
            bool replaced = false;
            _ = core.ExecuteScriptAsync("(() => { const end = Date.now() + 3000; while (Date.now() < end) {} })()");
            await Task.Delay(100);
            await window.Web.ReplaceIfStillUnresponsiveAsync(core, () => replaced = true);
            Check(replaced, "renderer still busy after the grace period is replaced", checks);
            await Task.Delay(3500);
            replaced = false;
            WebViewService.UnresponsiveGrace = TimeSpan.FromSeconds(20);
            await window.Web.ReplaceIfStillUnresponsiveAsync(core, () => replaced = true);
            Check(!replaced, "responsive renderer is kept", checks);

            // Unread badge from a synthetic Messages link on the signed-out page.
            async Task<bool> WaitUntil(Func<bool> condition)
            {
                for (int i = 0; i < 40 && !condition(); i++) await Task.Delay(100);
                return condition();
            }
            await core.ExecuteScriptAsync("""
                (() => {
                    const a = document.createElement('a');
                    a.id = 'instadesktop-unread-fixture'; a.href = '/direct/inbox/';
                    a.style.cssText = 'position:fixed;left:0;top:0;display:block;width:40px;height:20px;z-index:99999';
                    a.innerHTML = '<span>3</span>';
                    document.body.appendChild(a);
                })()
                """);
            Check(await WaitUntil(() => window.Web.UnreadCount == 3) && window.Title == "(3) InstaDesktop" &&
                window.TaskbarItemInfo?.Overlay is not null && window.ShownUnreadCount == 3, "unread count shown on taskbar and title", checks);
            // Saved next to the report for a visual check of the badge graphics.
            if (window.TaskbarItemInfo?.Overlay is System.Windows.Media.Imaging.BitmapSource overlay)
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(overlay));
                await using var png = File.Create(Path.ChangeExtension(output, ".taskbar-badge.png"));
                encoder.Save(png);
            }
            window.SaveTrayIconForDiagnostics(Path.ChangeExtension(output, ".tray-badge.png"));
            foreach (var (count, light, name) in new[] { (3, false, "dark-3"), (3, true, "light-3"), (12, false, "dark-12"), (12, true, "light-12") })
            {
                var badgeEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                badgeEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create((System.Windows.Media.Imaging.BitmapSource)MainWindow.CreateOverlay(count, light)));
                await using var badgePng = File.Create(Path.ChangeExtension(output, ".taskbar-badge-" + name + ".png"));
                badgeEncoder.Save(badgePng);
            }
            await settings.UpdateAsync(s => s.UnreadBadge = false);
            Check(await WaitUntil(() => window.Title == "InstaDesktop") && window.TaskbarItemInfo?.Overlay is null,
                "unread badge can be turned off", checks);
            await settings.UpdateAsync(s => s.UnreadBadge = true);
            await core.ExecuteScriptAsync("document.querySelector('#instadesktop-unread-fixture span').textContent = '0'");
            Check(await WaitUntil(() => window.Web.UnreadCount == 0) && window.Title == "InstaDesktop" && window.TaskbarItemInfo?.Overlay is null,
                "read messages clear the badge", checks);
            await core.ExecuteScriptAsync("document.getElementById('instadesktop-unread-fixture').remove()");

            // Pause notifications.
            Check(window.NotificationsActive == settings.Current.AppNotifications, "notifications active when not paused", checks);
            await window.PauseNotificationsAsync(DateTimeOffset.Now.AddHours(1));
            Check(!window.NotificationsActive && settings.Current.NotificationsPausedUntil is not null, "pause holds notifications back", checks);
            await window.PauseNotificationsAsync(null);
            Check(window.NotificationsActive == settings.Current.AppNotifications && settings.Current.NotificationsPausedUntil is null,
                "resume notifications", checks);
            var lateEvening = new DateTimeOffset(2026, 10, 6, 23, 30, 0, TimeSpan.FromHours(8));
            Check(MainWindow.TomorrowMorning(lateEvening) == new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.FromHours(8)),
                "pause until tomorrow ends at 8:00", checks);

            // Downloads go straight to the configured folder.
            string downloads = Path.Combine(AppPaths.Root, "downloads-check");
            await settings.UpdateAsync(s => { s.DownloadFolder = downloads; s.AskDownloadLocation = false; });
            await window.Web.ApplySettingsAsync();
            Check(string.Equals(core.Profile.DefaultDownloadFolderPath, downloads, StringComparison.OrdinalIgnoreCase),
                "download folder applied to the profile", checks);
            await core.ExecuteScriptAsync("""
                (() => {
                    const a = document.createElement('a');
                    a.href = URL.createObjectURL(new Blob(['InstaDesktop download check'], { type: 'text/plain' }));
                    a.download = 'instadesktop-download-check.txt';
                    document.body.appendChild(a); a.click(); a.remove();
                })()
                """);
            string downloaded = Path.Combine(downloads, "instadesktop-download-check.txt");
            Check(await WaitUntil(() => File.Exists(downloaded)), "download saved without asking", checks);
            await settings.UpdateAsync(s => s.DownloadFolder = null);
            await window.Web.ApplySettingsAsync();

            // Call windows: always on top from the setting and the title bar menu.
            CallWindow.DiagnosticHidden = true;
            var call = new CallWindow(600, 400, null, onTop: true);
            try
            {
                call.Show();
                Check(call.Topmost && call.OnTopMenuChecked, "call window starts on top with the menu checked", checks);
                SendMessage(new System.Windows.Interop.WindowInteropHelper(call).Handle, 0x0112, new IntPtr(0x1000), IntPtr.Zero);
                Check(!call.Topmost && !call.OnTopMenuChecked, "title bar menu turns always on top off", checks);
            }
            finally { call.Close(); CallWindow.DiagnosticHidden = false; }

            // Light and dark: app brushes, Instagram's color scheme, WebView background.
            Brush Resource(string key) => (Brush)Application.Current.Resources[key];
            ThemeService.Use(AppTheme.Light);
            Check(!ThemeService.IsDark && ((SolidColorBrush)Resource("AppBackground")).Color == Colors.White &&
                core.Profile.PreferredColorScheme == CoreWebView2PreferredColorScheme.Light &&
                window.Web.View!.DefaultBackgroundColor.ToArgb() == System.Drawing.Color.White.ToArgb(), "light theme applies to the app and Instagram", checks);
            ThemeService.Use(AppTheme.Dark);
            Check(ThemeService.IsDark && ((SolidColorBrush)Resource("AppBackground")).Color == Colors.Black &&
                core.Profile.PreferredColorScheme == CoreWebView2PreferredColorScheme.Dark, "dark theme restores", checks);

            // Traditional Chinese interface text.
            Check(Loc.IsTraditionalChinese(new System.Globalization.CultureInfo("zh-TW")) && Loc.IsTraditionalChinese(new System.Globalization.CultureInfo("zh-HK")) &&
                !Loc.IsTraditionalChinese(new System.Globalization.CultureInfo("zh-CN")) && !Loc.IsTraditionalChinese(new System.Globalization.CultureInfo("en-US")),
                "Windows language picks Traditional Chinese only for zh-TW/HK/MO", checks);
            Loc.Use(UiLanguage.TraditionalChinese);
            bool chinese = Loc.T("Settings.Title") == "設定" && Loc.F("Main.UnreadMany", 3) == "3 則未讀訊息";
            var zhSettings = new SettingsWindow(settings, window.Web) { Owner = window, ShowInTaskbar = false, Opacity = 0 };
            zhSettings.Show();
            zhSettings.UpdateLayout();
            chinese &= zhSettings.Title == "InstaDesktop 設定";
            zhSettings.Close();
            Loc.Use(UiLanguage.English);
            Check(chinese && Loc.T("Settings.Title") == "Settings", "Traditional Chinese text in code and XAML", checks);

            // The messages window: one inbox window on the same profile.
            PopoutWindow.DiagnosticHidden = true;
            try
            {
                Check(await window.Web.OpenMessagesWindowAsync() && window.Web.MessagesWindow is { } messages &&
                    await WaitUntil(() => messages.View.CoreWebView2?.Source.StartsWith("https://www.instagram.com/", StringComparison.Ordinal) == true),
                    "messages window opens Instagram in its own window", checks);
                var first = window.Web.MessagesWindow!;
                Check(await window.Web.OpenMessagesWindowAsync() && window.Web.MessagesWindow == first, "opening it again reuses the window", checks);
                Check(first.WindowStyle == WindowStyle.None && first.Title == Loc.T("Web.MessagesTitle"), "floating panel without a browser title bar", checks);
                // The panel's own look, for a visual check (the page area is blank here).
                first.SetUnread(3);
                foreach (var (theme, suffix) in new[] { (AppTheme.Dark, "dark"), (AppTheme.Light, "light") })
                {
                    ThemeService.Use(theme);
                    if (first.Content is not FrameworkElement panel) continue;
                    panel.UpdateLayout();
                    var shot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    shot.Render(panel);
                    var panelEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    panelEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
                    await using var panelPng = File.Create(Path.ChangeExtension(output, ".messages-panel-" + suffix + ".png"));
                    panelEncoder.Save(panelPng);
                }
                ThemeService.Use(AppTheme.Dark);
                first.SetUnread(4);
                bool unreadShown = first.UnreadShown == "4";
                first.SetUnread(0);
                Check(unreadShown && first.UnreadShown == "", "panel header shows the unread count", checks);
                first.SetOnTop(true);
                Check(first.Topmost && first.OnTopMenuChecked, "pin keeps the panel on top", checks);
                first.SetOnTop(false);
                first.Width = 500;
                first.Close();
                Check(await WaitUntil(() => !first.IsVisible && window.Web.MessagesWindow == first && Math.Abs(settings.Current.MessagesWidth - 500) < 1),
                    "closing hides the panel and remembers its size", checks);
                Check(await window.Web.ToggleMessagesWindowAsync() && first.IsVisible && window.Web.MessagesWindow == first,
                    "the panel comes back instantly", checks);
                var mainNavigation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnMainNavigation(object? sender, CoreWebView2NavigationStartingEventArgs e) => mainNavigation.TrySetResult(e.Uri);
                core.NavigationStarting += OnMainNavigation;
                var mainLoad = WaitForLoadAsync(window.Web);
                WebViewService.MessagesIdleClose = TimeSpan.FromSeconds(1);
                first.RequestOpenInMain();
                string openedInMain = await mainNavigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
                core.NavigationStarting -= OnMainNavigation;
                await mainLoad;
                Check(NavigationPolicy.IsTrusted(openedInMain) && !first.IsVisible, "open in main window shows the page there and hides the panel", checks);
                Check(await WaitUntil(() => window.Web.MessagesWindow is null), "a panel hidden for a while is closed to free memory", checks);
                WebViewService.MessagesIdleClose = TimeSpan.FromMinutes(10);
            }
            finally { PopoutWindow.DiagnosticHidden = false; }

            // Clear cache keeps the profile signed in (local storage survives).
            await core.ExecuteScriptAsync("localStorage.setItem('instadesktop-cache-check', 'kept');");
            var cacheReload = WaitForLoadAsync(window.Web);
            Check(await window.Web.ClearCacheAsync() == true, "clear cache succeeds", checks);
            await cacheReload;
            Check(await core.ExecuteScriptAsync("localStorage.getItem('instadesktop-cache-check')") == "\"kept\"", "clear cache keeps site storage", checks);
            await core.ExecuteScriptAsync("localStorage.removeItem('instadesktop-cache-check');");

            // Jump list entries and the channel a second launch uses.
            var jump = AppJumpList.Build();
            Check(jump.JumpItems.Count == 7 && jump.JumpItems.OfType<System.Windows.Shell.JumpTask>().All(t => AppCommands.Parse(t.Arguments) != AppCommand.None),
                "jump list entries carry known commands", checks);
            Check(AppCommands.Parse("--OPEN=Reels") == AppCommand.Reels && AppCommands.Parse("--open=https://evil.test") == AppCommand.None &&
                AppCommands.Find(new[] { "--background", "--messages-window" }) == AppCommand.MessagesWindow, "only fixed commands are accepted", checks);
            var receivedCommand = new TaskCompletionSource<AppCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var channel = new InstanceChannel("InstaDesktop-Test-" + Guid.NewGuid().ToString("N")))
            {
                channel.Listen(command => receivedCommand.TrySetResult(command));
                await Task.Delay(200);
                bool sent = await Task.Run(() => new InstanceChannel(channel.Name).Send(AppCommand.Explore));
                Check(sent && await receivedCommand.Task.WaitAsync(TimeSpan.FromSeconds(5)) == AppCommand.Explore, "a second launch hands its command over", checks);
            }
            var commandStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCommandNavigation(object? sender, CoreWebView2NavigationStartingEventArgs e)
            { if (e.Uri.Contains("/reels/", StringComparison.Ordinal)) commandStarted.TrySetResult(e.Uri); }
            core.NavigationStarting += OnCommandNavigation;
            var commandLoad = WaitForLoadAsync(window.Web);
            window.RunCommand(AppCommand.Reels);
            await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            core.NavigationStarting -= OnCommandNavigation;
            await commandLoad;
            Check(true, "jump list Reels opens Reels", checks);

            // Taskbar flash only when none of the app's windows is in front.
            await settings.UpdateAsync(s => s.FlashTaskbar = false);
            int flashes = window.FlashCount;
            window.FlashForNewMessage();
            bool offRespected = window.FlashCount == flashes;
            await settings.UpdateAsync(s => s.FlashTaskbar = true);
            bool anyActive = Application.Current.Windows.OfType<Window>().Any(w => w.IsActive);
            window.FlashForNewMessage();
            Check(offRespected && window.FlashCount == flashes + (anyActive ? 0 : 1), "taskbar flash follows the setting and focus", checks);

            // Muting a conversation from its toast.
            await window.MuteConversationAsync("https://www.instagram.com/direct/t/4242/");
            await window.MuteConversationAsync("https://evil.test/direct/t/1/");
            Check(settings.Current.IsMuted("https://www.instagram.com/direct/t/4242/", DateTimeOffset.UtcNow) &&
                settings.Current.MutedConversations.Count == 1, "mute stores only a valid conversation for 8 hours", checks);
            var reloadedMutes = new SettingsService();
            await reloadedMutes.LoadAsync();
            Check(reloadedMutes.Current.IsMuted("https://www.instagram.com/direct/t/4242/", DateTimeOffset.UtcNow), "muted conversations survive a restart", checks);
            await settings.UpdateAsync(s => s.MutedConversations = new());

            var appearance = settings.Current.Copy();
            appearance.DeveloperTools = true;
            await settings.SaveAsync(appearance);
            await window.Web.ApplySettingsAsync();
            Check(core.Settings.AreDevToolsEnabled, "devtools apply without reload", checks);
            appearance.DeveloperTools = false;
            await settings.SaveAsync(appearance);
            await window.Web.ApplySettingsAsync();
            window.SetMediaFullscreen(true);
            Check(window.TitleBar.Visibility == Visibility.Collapsed, "fullscreen hides the title bar", checks);
            window.SetMediaFullscreen(false);
            Check(window.TitleBar.Visibility == Visibility.Visible, "fullscreen restores the title bar", checks);

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
            Check(await core.ExecuteScriptAsync("localStorage.getItem('instadesktop-diagnostic-marker')") == "\"profile-roundtrip\"", "profile storage survives reload", checks);

            // Recreate the controller exactly as renderer-crash recovery does.
            var recreate = WaitForLoadAsync(window.Web);
            await window.Web.InitializeAsync();
            await recreate;
            core = window.Web.Core!;
            Check(await core.ExecuteScriptAsync("localStorage.getItem('instadesktop-diagnostic-marker')") == "\"profile-roundtrip\"", "profile storage survives controller recreation", checks);
            await core.ExecuteScriptAsync("localStorage.removeItem('instadesktop-diagnostic-marker');");

            // A load that fails while offline recovers when Windows reports the
            // network again, before the regular retry delay.
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions",
                "{\"offline\":true,\"latency\":0,\"downloadThroughput\":-1,\"uploadThroughput\":-1}");
            var offline = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOffline(string message, bool failed) { if (failed) offline.TrySetResult(message); }
            window.Web.StatusChanged += OnOffline;
            core.Navigate("https://instadesktop-offline-check.instagram.com/");
            string offlineMessage = await offline.Task.WaitAsync(TimeSpan.FromSeconds(15));
            window.Web.StatusChanged -= OnOffline;
            Check(window.Web.NeedsRecovery && offlineMessage.Contains("retry automatically", StringComparison.Ordinal),
                "offline load failure says it will retry", checks);
            var attempt = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
            LoggingService.DiagnosticObserver = (name, _, _) => { if (name == LogEvent.AutoRecoveryAttempt) attempt.TrySetResult(DateTimeOffset.UtcNow); };
            var networkBack = DateTimeOffset.UtcNow;
            var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOnline() => online.TrySetResult();
            window.Web.Ready += OnOnline;
            window.Web.OnNetworkAvailable();
            try
            {
                var attemptAt = await attempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
                checks["networkRetrySeconds"] = Math.Round((attemptAt - networkBack).TotalSeconds, 1);
                Check((attemptAt - networkBack).TotalSeconds < 4.5, "network return triggers the retry", checks);
                await online.Task.WaitAsync(TimeSpan.FromSeconds(45));
            }
            finally { window.Web.Ready -= OnOnline; LoggingService.DiagnosticObserver = null; }
            core = window.Web.Core!;
            Check(!window.Web.NeedsRecovery && NavigationPolicy.IsTrusted(core.Source), "page loads again after the network returns", checks);

            // Kill only this diagnostic profile's own WebView browser process. The
            // app must come back by itself, without Retry (tray sessions).
            var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCrash(string message, bool failed) { if (failed) crashed.TrySetResult(); }
            window.Web.StatusChanged += OnCrash;
            using (var browserProcess = Process.GetProcessById((int)core.BrowserProcessId)) browserProcess.Kill();
            await crashed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            window.Web.StatusChanged -= OnCrash;
            Check(window.Web.NeedsRecovery, "browser crash shows recovery UI", checks);
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRecovered() => recovered.TrySetResult();
            window.Web.Ready += OnRecovered;
            try { await recovered.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
            finally { window.Web.Ready -= OnRecovered; }
            core = window.Web.Core!;
            Check(NavigationPolicy.IsTrusted(core.Source) && !window.Web.NeedsRecovery, "crashed browser recovers automatically", checks);

            // Render settings without changing the user's startup registration.
            var settingsWindow = new SettingsWindow(settings, window.Web) { Owner = window, ShowInTaskbar = false, Opacity = 0 };
            settingsWindow.Show();
            settingsWindow.UpdateLayout();
            Check(settingsWindow.IsLoaded, "settings window opens", checks);
            Check(Loc.MissingKeys.Count == 0, "every interface text has an English and Chinese entry", checks);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

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
