using System;

namespace InstaDesktop.Models;

// Numeric order is the presentation priority, lowest first.
public enum NotificationSource { NativeWebView, PageNotification, DirectDom, UnreadBadge }
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
    public string Origin { get; init; } = "https://www.instagram.com";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool HasSourceTimestamp { get; init; }
    public bool Silent { get; init; }
    public NotificationSource Source { get; init; }
}
