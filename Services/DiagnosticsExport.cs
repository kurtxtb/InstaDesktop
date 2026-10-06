using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using InstaDesktop.Localization;
using InstaDesktop.Models;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop.Services;

// Settings > Advanced > Export diagnostics: a zip to attach to a bug report.
// The app log holds only fixed event names, exception types and numbers
// (see LoggingService); the summary adds versions and the shape of the
// settings. No page addresses, conversation ids, names or folder paths.
internal static class DiagnosticsExport
{
    public static string DefaultFileName =>
        "InstaDesktop-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip";

    public static async Task ExportAsync(string zipPath, AppSettings settings)
    {
        string temporary = zipPath + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "app.log", "app.log.1" })
            {
                string path = Path.Combine(AppPaths.Logs, name);
                if (!File.Exists(path)) continue;
                // The app may be writing to it right now.
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var target = zip.CreateEntry("logs/" + name, CompressionLevel.Optimal).Open();
                await source.CopyToAsync(target);
            }
            var entry = zip.CreateEntry("summary.json", CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, Summary(settings), new JsonSerializerOptions { WriteIndented = true });
        }
        File.Move(temporary, zipPath, overwrite: true);
        LoggingService.Write(LogEvent.DiagnosticsExported);
    }

    internal static object Summary(AppSettings s)
    {
        string? webView;
        try { webView = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or COMException) { webView = null; }
        var version = typeof(App).Assembly.GetName().Version;
        return new
        {
            exportedAtUtc = DateTimeOffset.UtcNow,
            app = version is null ? null : $"{version.Major}.{version.Minor}.{version.Build}",
            os = RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            dotnet = RuntimeInformation.FrameworkDescription,
            webView2Runtime = webView,
            windowsLanguage = CultureInfo.CurrentUICulture.Name,
            interfaceChinese = Loc.Chinese,
            darkTheme = ThemeService.IsDark,
            settings = new
            {
                s.StartWithWindows, closeButton = s.CloseButton.ToString(), theme = s.Theme.ToString(), language = s.Language.ToString(),
                s.AppNotifications, s.NotificationSound, s.HideNotificationContent, s.FlashTaskbar, s.UnreadBadge,
                notificationsPaused = s.NotificationsPaused(DateTimeOffset.UtcNow),
                mutedConversations = s.MutedConversations.Count(m => m.Value > DateTimeOffset.UtcNow),
                openNotificationsIn = s.OpenNotificationsIn.ToString(),
                globalShortcutShowWindow = !string.IsNullOrEmpty(s.HotkeyShowWindow),
                globalShortcutMessages = !string.IsNullOrEmpty(s.HotkeyMessages),
                s.RememberLastPage, s.AllowMicrophone, s.AllowCamera, s.CallWindowsOnTop, s.AutoUpdate,
                customDownloadFolder = s.DownloadFolder is not null, s.AskDownloadLocation,
                s.HardwareAcceleration, s.BackgroundLowMemory, s.DeveloperTools, s.ZoomFactor, s.Maximized
            }
        };
    }
}
