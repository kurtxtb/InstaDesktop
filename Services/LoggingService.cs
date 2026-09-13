using System;
using System.IO;
using System.Text;
using InstaDesktop.Models;

namespace InstaDesktop.Services;

public enum LogEvent
{
    AppStarted, WebViewInitializationError, InjectionError, NavigationError,
    WebViewProcessError, NotificationReceived, DesktopNotificationShown, UnexpectedException,
    NotificationPermission, NotificationCandidate, NotificationDuplicate, NotificationAccepted,
    NotificationBaseline, NotificationDiagnostic, NotificationRejected, NotificationRegistered,
    NotificationInitializationFailed, NotificationImageFailed, NotificationLifecycleFailed,
    WindowsNotificationRequested, WindowsNotificationSubmitted, WindowsNotificationUnavailable,
    WindowsNotificationFailed, NotificationActivated, NotificationActivationFailed, NotificationNavigated
}

public static class LoggingService
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    // Source + body length + presence bits only. No body, name, URL, tag or token.
    public static void Notification(LogEvent name, InstagramNotification n) =>
        Write(name, code: (int)n.Source * 100000 + Math.Min(n.Body.Length, 1000) * 10 +
            (string.IsNullOrEmpty(n.SenderName) ? 0 : 1) + (n.AvatarUrl is null ? 0 : 2) + (n.ThreadUrl is null ? 0 : 4));

    // Only fixed event names, exception types and numeric codes enter the log.
    // Exception messages, stack traces, web content and URLs can contain secrets.
    public static void Write(LogEvent name, Exception? exception = null, int? code = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                string path = Path.Combine(AppPaths.Logs, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                string detail = exception is null ? "" : $" {exception.GetType().Name} HResult={exception.HResult}";
                if (code.HasValue) detail += $" Code={code.Value}";
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {name}{detail}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
