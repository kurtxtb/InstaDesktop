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

internal sealed class WindowsNotificationService : IWindowsNotificationPresenter
{
    public event Action<string, bool>? Dismissed;
    public event Action<string>? Failed;

    // The compat package handles per-user registration and COM activation for
    // unpackaged WPF, without a Windows App SDK runtime/bootstrapper dependency.
    public static void Register(Action<string> activation)
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += args => activation(args.Argument);
            LoggingService.Write(LogEvent.NotificationRegistered);
        }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationInitializationFailed, error); }
    }

    public bool Show(InstagramNotification n, string token, string? avatar, string? image, bool replace)
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                LoggingService.Write(LogEvent.WindowsNotificationUnavailable, code: (int)notifier.Setting);
                return false;
            }
            var builder = new ToastContentBuilder().AddArgument("notification", token)
                .AddArgument("thread", n.ThreadUrl ?? "").AddText(n.Title).AddText(n.Body);
            if (avatar is not null) builder.AddAppLogoOverride(new Uri(avatar), ToastGenericAppLogoCrop.Circle);
            if (image is not null) builder.AddHeroImage(new Uri(image));
            if (n.Silent) builder.AddAudio(new ToastAudio { Silent = true });
            var toast = new ToastNotification(builder.GetToastContent().GetXml())
            {
                Tag = token, Group = "instagram", SuppressPopup = replace,
                ExpirationTime = DateTimeOffset.UtcNow.AddDays(1)
            };
            toast.Dismissed += (_, args) => Dismissed?.Invoke(token, args.Reason == ToastDismissalReason.UserCanceled);
            toast.Failed += (_, args) =>
            {
                LoggingService.Write(LogEvent.WindowsNotificationFailed, args.ErrorCode);
                Failed?.Invoke(token);
            };
            LoggingService.Write(LogEvent.WindowsNotificationRequested);
            notifier.Show(toast);
            // Show returning successfully means Windows accepted the request;
            // Windows provides no confirmation that a banner was visible (DND).
            LoggingService.Write(LogEvent.WindowsNotificationSubmitted);
            return true;
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.WindowsNotificationFailed, error);
            return false;
        }
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
