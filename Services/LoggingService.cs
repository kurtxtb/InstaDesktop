using System;
using System.IO;
using System.Text;

namespace InstaDesktop.Services;

public enum LogEvent
{
    AppStarted, WebViewInitializationError, InjectionError, NavigationError,
    WebViewProcessError, NotificationReceived, DesktopNotificationShown, UnexpectedException
}

public static class LoggingService
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

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
