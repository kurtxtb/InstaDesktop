using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstaDesktop.Localization;
using InstaDesktop.Services;

namespace InstaDesktop.Models;

public enum CloseButtonBehavior { MinimizeToTray, ExitApplication }
public enum NotificationOpenTarget { MainWindow, MessagesPanel }

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public CloseButtonBehavior CloseButton { get; set; } = CloseButtonBehavior.MinimizeToTray;
    public bool HardwareAcceleration { get; set; } = true;
    public bool DeveloperTools { get; set; }
    public bool BackgroundLowMemory { get; set; } = true;
    public bool AppNotifications { get; set; } = true;
    public bool NotificationSound { get; set; } = true;
    // Desktop notifications are held back until this time (tray > Pause notifications).
    public DateTimeOffset? NotificationsPausedUntil { get; set; }
    public bool UnreadBadge { get; set; } = true;
    public bool FlashTaskbar { get; set; } = true;
    // Notifications name the sender only: no message text, no image.
    public bool HideNotificationContent { get; set; }
    // Direct thread URL -> muted until. Replace the dictionary to change it:
    // Copy() is shallow and Current must never change underneath a reader.
    public Dictionary<string, DateTimeOffset> MutedConversations { get; set; } = new();
    // Where clicking a message notification opens the conversation.
    public NotificationOpenTarget OpenNotificationsIn { get; set; } = NotificationOpenTarget.MainWindow;
    // System-wide shortcuts ("Ctrl+Alt+I"); empty turns one off.
    public string HotkeyShowWindow { get; set; } = "Ctrl+Alt+I";
    public string HotkeyMessages { get; set; } = "Ctrl+Alt+M";
    // Reopen on the page used last (path only, never sign-in or call pages).
    public bool RememberLastPage { get; set; } = true;
    public string? LastPage { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public UiLanguage Language { get; set; } = UiLanguage.System;
    public bool AllowMicrophone { get; set; }
    public bool AllowCamera { get; set; }
    public bool CallWindowsOnTop { get; set; }
    // 1 = the one-time recovery of media denials saved by builds <= 1.1.0 ran.
    public int MediaPermissionRevision { get; set; }
    public bool AutoUpdate { get; set; } = true;
    // The version that ran last; a different one at startup means an update.
    public string? LastRunVersion { get; set; }
    public bool AskDownloadLocation { get; set; }
    // null = the Windows Downloads folder.
    public string? DownloadFolder { get; set; }
    public double ZoomFactor { get; set; } = 1;
    public double? MessagesLeft { get; set; }
    public double? MessagesTop { get; set; }
    public double MessagesWidth { get; set; } = 440;
    public double MessagesHeight { get; set; } = 720;
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 800;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }

    public AppSettings Copy() => (AppSettings)MemberwiseClone();

    public bool NotificationsPaused(DateTimeOffset now) => NotificationsPausedUntil is { } until && until > now;

    public bool IsMuted(string? thread, DateTimeOffset now) =>
        thread is not null && MutedConversations.TryGetValue(thread, out var until) && until > now;

    public const int MaxMutedConversations = 200;

    public void Normalize()
    {
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 760, 16384) : 1200;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 600, 16384) : 800;
        if (Left is double left && !double.IsFinite(left)) Left = null;
        if (Top is double top && !double.IsFinite(top)) Top = null;
        if (!Enum.IsDefined(CloseButton)) CloseButton = CloseButtonBehavior.MinimizeToTray;
        ZoomFactor = double.IsFinite(ZoomFactor) ? Math.Clamp(ZoomFactor, 0.25, 5) : 1;
        if (string.IsNullOrWhiteSpace(DownloadFolder) || !Path.IsPathFullyQualified(DownloadFolder.Trim())) DownloadFolder = null;
        else DownloadFolder = DownloadFolder.Trim();
        if (LastRunVersion is { Length: > 32 }) LastRunVersion = null;
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        if (!Enum.IsDefined(OpenNotificationsIn)) OpenNotificationsIn = NotificationOpenTarget.MainWindow;
        HotkeyShowWindow = Hotkey.Parse(HotkeyShowWindow)?.ToString() ?? "";
        HotkeyMessages = Hotkey.Parse(HotkeyMessages)?.ToString() ?? "";
        if (!RememberLastPage || WebViewService.LastPageOf(LastPage) is not { } page) LastPage = null;
        else LastPage = page;
        if (!Enum.IsDefined(Language)) Language = UiLanguage.System;
        MessagesWidth = double.IsFinite(MessagesWidth) ? Math.Clamp(MessagesWidth, 340, 720) : 440;
        MessagesHeight = double.IsFinite(MessagesHeight) ? Math.Clamp(MessagesHeight, 480, 4000) : 720;
        if (MessagesLeft is double ml && !double.IsFinite(ml)) MessagesLeft = null;
        if (MessagesTop is double mt && !double.IsFinite(mt)) MessagesTop = null;
        // Only valid Direct thread URLs that are still muted, newest first, bounded.
        var now = DateTimeOffset.UtcNow;
        MutedConversations = (MutedConversations ?? new())
            .Where(m => m.Value > now && NotificationPolicy.DirectUrl(m.Key) == m.Key)
            .OrderByDescending(m => m.Value).Take(MaxMutedConversations)
            .ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
    }
}
