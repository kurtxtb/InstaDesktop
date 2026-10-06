using System;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using InstaDesktop.Models;

namespace InstaDesktop.Services;

internal interface IWindowsNotificationPresenter
{
    event Action<string, bool>? Dismissed;
    event Action<string>? Failed;
    bool Show(InstagramNotification notification, string token, string? avatar, string? image, bool replace);
    void Remove(string token);
}

// The only surface Show() talks to. Lets diagnostics exercise the real presenter
// (setting checks, Show/Failed diagnostics) without registering a real toast.
internal interface IToastNotifierAdapter
{
    NotificationSetting Setting { get; }
    void Show(ToastNotification toast);
}

internal sealed class WindowsNotificationService : IWindowsNotificationPresenter
{
    private sealed class CompatNotifier : IToastNotifierAdapter
    {
        private readonly ToastNotifierCompat _notifier = ToastNotificationManagerCompat.CreateToastNotifier();
        public NotificationSetting Setting => _notifier.Setting;
        public void Show(ToastNotification toast) => _notifier.Show(toast);
    }

    private readonly Func<IToastNotifierAdapter> _createNotifier;
    public event Action<string, bool>? Dismissed;
    public event Action<string>? Failed;

    public WindowsNotificationService() : this(() => new CompatNotifier()) { }
    internal WindowsNotificationService(Func<IToastNotifierAdapter> createNotifier) => _createNotifier = createNotifier;

    // The compat package handles per-user registration and COM activation for
    // unpackaged WPF, without a Windows App SDK runtime/bootstrapper dependency.
    public static void Register(Action<string> activation)
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += args => activation(args.Argument);
            LoggingService.Write(LogEvent.NotificationRegistered);
            // A closed-app toast click launches this process; record it so a
            // missing restore/navigation can be told apart from a missing click.
            if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
                LoggingService.Write(LogEvent.NotificationColdActivation);
            // Record the OS state at startup, before any message arrives, so a
            // system-level disable is visible in the log even with no traffic.
            ReadSetting(new CompatNotifier());
        }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationInitializationFailed, error); }
    }

    // Returns false only for a definite OS/user/policy disable. An unreadable
    // setting (some unpackaged registrations reject the query) is logged and
    // Show is still attempted, rather than silently dropping every toast.
    internal static bool ReadSetting(IToastNotifierAdapter notifier)
    {
        NotificationSetting setting;
        try { setting = notifier.Setting; }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.WindowsNotificationSettingUnavailable, error);
            return true;
        }
        LoggingService.Write(LogEvent.WindowsNotificationSetting, code: (int)setting);
        if (setting == NotificationSetting.Enabled) return true;
        LoggingService.Write(setting switch
        {
            NotificationSetting.DisabledForApplication => LogEvent.WindowsNotificationDisabledForApplication,
            NotificationSetting.DisabledForUser => LogEvent.WindowsNotificationDisabledForUser,
            NotificationSetting.DisabledByGroupPolicy => LogEvent.WindowsNotificationDisabledByGroupPolicy,
            NotificationSetting.DisabledByManifest => LogEvent.WindowsNotificationDisabledByManifest,
            _ => LogEvent.WindowsNotificationUnavailable
        }, code: (int)setting);
        return false;
    }

    public bool Show(InstagramNotification n, string token, string? avatar, string? image, bool replace)
    {
        IToastNotifierAdapter notifier;
        try { notifier = _createNotifier(); }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.WindowsNotificationUnavailable, error);
            return false;
        }
        // Respect OS/user/policy disables; no tray balloon substitute is shown.
        if (!ReadSetting(notifier)) return false;
        try
        {
            var builder = new ToastContentBuilder().AddArgument("notification", token)
                .AddArgument("thread", n.ThreadUrl ?? "").AddText(n.Title).AddText(n.Body);
            if (avatar is not null) builder.AddAppLogoOverride(new Uri(avatar), ToastGenericAppLogoCrop.Circle);
            if (image is not null) builder.AddHeroImage(new Uri(image));
            if (n.GroupedCount > 1)
                builder.AddAttributionText(InstaDesktop.Localization.Loc.F("Notify.EarlierMessages", n.GroupedCount - 1));
            if (n.Silent) builder.AddAudio(new ToastAudio { Silent = true });
            // Only a known conversation can be muted; the click is handled in the background.
            if (n.ThreadUrl is not null)
                builder.AddButton(new ToastButton().SetContent(InstaDesktop.Localization.Loc.T("Notify.Mute"))
                    .AddArgument("action", "mute").AddArgument("notification", token).AddArgument("thread", n.ThreadUrl)
                    .SetBackgroundActivation());
            var toast = new ToastNotification(builder.GetToastContent().GetXml())
            {
                Tag = token, Group = "instagram", SuppressPopup = replace,
                ExpirationTime = DateTimeOffset.UtcNow.AddDays(1)
            };
            toast.Dismissed += (_, args) => ReportDismissed(token, args.Reason);
            toast.Failed += (_, args) => ReportDeliveryFailed(token, args.ErrorCode);
            LoggingService.Write(LogEvent.WindowsNotificationRequested, code: replace ? 1 : 0);
            notifier.Show(toast);
            // Show returning successfully means Windows accepted the request;
            // Windows provides no confirmation that a banner was visible (DND).
            LoggingService.Write(LogEvent.WindowsNotificationSubmitted, code: replace ? 1 : 0);
            return true;
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.WindowsNotificationShowFailed, error);
            return false;
        }
    }

    // Raised by Windows after an accepted Show (e.g. invalid image/content).
    internal void ReportDeliveryFailed(string token, Exception? error)
    {
        LoggingService.Write(LogEvent.WindowsNotificationDeliveryFailed, error);
        Failed?.Invoke(token);
    }

    internal void ReportDismissed(string token, ToastDismissalReason reason)
    {
        LoggingService.Write(LogEvent.WindowsNotificationDismissed, code: (int)reason);
        Dismissed?.Invoke(token, reason == ToastDismissalReason.UserCanceled);
    }

    public void Remove(string token)
    {
        try { ToastNotificationManagerCompat.History.Remove(token, "instagram"); }
        catch (Exception error) { LoggingService.Write(LogEvent.WindowsNotificationFailed, error); }
    }

    public static void Uninstall()
    {
        try { ToastNotificationManagerCompat.Uninstall(); }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationInitializationFailed, error); }
    }
}
