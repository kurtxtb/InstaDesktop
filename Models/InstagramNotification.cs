using System;

namespace InstaDesktop.Models;

// Numeric order is the presentation priority, lowest first.
public enum NotificationSource { NativeWebView, PageNotification, DirectDom, UnreadBadge, DirectInbox }
public enum InstagramNotificationType { Instagram, DirectMessage }

public sealed record InstagramNotification
{
    public string? Id { get; init; }
    public string? Tag { get; init; }
    public string? StateSequence { get; init; }
    public InstagramNotificationType Type { get; init; }
    public string Title { get; init; } = "Instagram";
    public string? SenderName { get; init; }
    public string Body { get; init; } = "You have a new message";
    public string? AvatarUrl { get; init; }
    public string? ImageUrl { get; init; }
    public string? ThreadUrl { get; init; }
    // Document-local row identity for button-based inbox layouts without an href.
    // Never use this as a navigation URL or persist it outside the delivery cache.
    public string? ConversationKey { get; init; }
    public string Origin { get; init; } = "https://www.instagram.com";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool HasSourceTimestamp { get; init; }
    public bool Silent { get; init; }
    public NotificationSource Source { get; init; }
    // Which controller raised a native event ("primary" or "monitor"). Both pages
    // can mirror one message; never shown, logged or used for navigation.
    public string? Emitter { get; init; }
    // Inbox preview-only deltas need independent message evidence before display.
    public bool RequiresNativeConfirmation { get; init; }
    // Only previews whose unread state is unavailable may use a unique nearby
    // badge increase as confirmation. Read rows and known-unread edits may not.
    public bool AllowsBadgeConfirmation { get; init; }
}
