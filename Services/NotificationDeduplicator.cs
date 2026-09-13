using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using InstaDesktop.Models;

namespace InstaDesktop.Services;

// UI-thread owned. Cache contains bounded, short-lived metadata; nothing is persisted.
public sealed class NotificationDeduplicator
{
    private sealed record Seen(InstagramNotification Notification, DateTimeOffset At, string Delivery);
    private readonly List<Seen> _seen = new();
    private static string Key(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    public string? Find(InstagramNotification n, DateTimeOffset now)
    {
        _seen.RemoveAll(x => now - x.At > TimeSpan.FromSeconds(30));
        foreach (var item in _seen.AsEnumerable().Reverse())
        {
            var p = item.Notification;
            if (p.Origin != n.Origin) continue;
            if (!string.IsNullOrEmpty(n.Id) && n.Id == p.Id) return item.Delivery;
            if (!string.IsNullOrEmpty(n.Id) && !string.IsNullOrEmpty(p.Id) && n.Id != p.Id) continue;
            if (n.Source == p.Source)
            {
                if (!string.IsNullOrEmpty(n.StateSequence) && n.StateSequence == p.StateSequence && n.ThreadUrl == p.ThreadUrl)
                    return item.Delivery;
                // Tags often identify a conversation, not a message. Only collapse a
                // tag with the same real source timestamp and payload, never tag alone.
                if (!string.IsNullOrEmpty(n.Tag) && n.Tag == p.Tag && n.HasSourceTimestamp && p.HasSourceTimestamp &&
                    n.Timestamp == p.Timestamp && Key(n.Body) == Key(p.Body)) return item.Delivery;
                continue;
            }
            if (now - item.At > TimeSpan.FromSeconds(3)) continue;
            // One event per source per delivery. A second native/page event with
            // identical text is evidence of a separate message, even in this window.
            if (_seen.Any(x => x.Delivery == item.Delivery && x.Notification.Source == n.Source)) continue;
            if (n.ThreadUrl is not null && p.ThreadUrl is not null && n.ThreadUrl != p.ThreadUrl) continue;
            bool badge = n.Source == NotificationSource.UnreadBadge || p.Source == NotificationSource.UnreadBadge;
            if (badge)
            {
                // An unread badge has no identity. Prefer known DMs; a native/page
                // alert within two seconds is the last-resort temporal correlation.
                // Concurrent unrelated activity can make this inherently ambiguous.
                if ((n.Type == InstagramNotificationType.DirectMessage && p.Type == InstagramNotificationType.DirectMessage) ||
                    (now - item.At <= TimeSpan.FromSeconds(2) &&
                     (n.Source <= NotificationSource.PageNotification || p.Source <= NotificationSource.PageNotification)))
                    return item.Delivery;
                continue;
            }
            if (Key(n.Body) == Key(p.Body) &&
                (Key(n.Title) == Key(p.Title) || (n.ThreadUrl is not null && n.ThreadUrl == p.ThreadUrl))) return item.Delivery;
        }
        return null;
    }

    public void Remember(InstagramNotification n, string delivery, DateTimeOffset now)
    {
        _seen.Add(new(n, now, delivery));
        if (_seen.Count > 256) _seen.RemoveRange(0, _seen.Count - 256);
    }

    public void Clear() => _seen.Clear();
}
