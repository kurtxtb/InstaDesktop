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
    WindowsNotificationFailed, NotificationActivated, NotificationActivationFailed, NotificationNavigated,
    DirectMonitorStarted, DirectMonitorReady, DirectMonitorBaselineCreated, DirectSnapshotChanged,
    DirectCandidateCreated, NativeCandidateAwaitingEnrichment, NativeCandidateEnriched,
    EnrichmentTimedOut, EnrichmentAmbiguous, DirectMonitorNavigationReset, DirectMonitorInitializationFailed,
    DirectMonitorExtraction, DirectMonitorUnreadState, DirectMonitorUncertainChange, DirectMonitorExtractionFailed,
    NotificationPermissionState, DirectMonitorPermission, DirectMonitorStopped,
    WindowsNotificationSetting, WindowsNotificationSettingUnavailable, WindowsNotificationDisabledForApplication,
    WindowsNotificationDisabledForUser, WindowsNotificationDisabledByGroupPolicy, WindowsNotificationDisabledByManifest,
    WindowsNotificationShowFailed, WindowsNotificationDeliveryFailed, WindowsNotificationDismissed, NotificationColdActivation,
    UpdateCheck, UpdateUnavailable, UpdateAvailable, UpdateVerificationFailed, UpdateLaunched,
    MediaPermission, MediaPermissionState, MediaPermissionSync, MediaPermissionPrompt, MediaPermissionCompletionFailed,
    MediaPermissionReset, MediaPermissionMigrated, MediaPermissionBlockedNotice, MediaPrivacySettingsOpened,
    NewWindowRequested, WindowCloseRequested, MonitorCaptureBlockFailed,
    CallWindowOpened, CallWindowClosed, CallWindowFailed,
    AutoRecoveryAttempt, UpdateDeferred, UpdateCleanupFailed, RendererUnresponsive,
    DownloadStarted, DownloadFolderUnavailable, NotificationsPaused, UpdateNotice,
    NotificationMuted, CacheCleared, CommandReceived, MessagesWindowOpened, MessagesWindowClosed, MessagesWindowFailed,
    HotkeysRegistered, DiagnosticsExported, MediaDownloaded, MediaDownloadFailed
}

public static class LoggingService
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    // Diagnostics only: lets the isolated test runner assert which fixed event
    // was written. Receives exactly what the log receives, never content.
    internal static Action<LogEvent, Exception?, int?>? DiagnosticObserver;

    // Source + body length + presence bits only. No body, name, URL, tag or token.
    public static void Notification(LogEvent name, InstagramNotification n) =>
        Write(name, code: (int)n.Source * 100000 + Math.Min(n.Body.Length, 1000) * 10 +
            (string.IsNullOrEmpty(n.SenderName) ? 0 : 1) + (n.AvatarUrl is null ? 0 : 2) + (n.ThreadUrl is null ? 0 : 4));

    // Only fixed event names, exception types and numeric codes enter the log.
    // Exception messages, stack traces, web content and URLs can contain secrets.
    public static void Write(LogEvent name, Exception? exception = null, int? code = null)
    {
        DiagnosticObserver?.Invoke(name, exception, code);
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
