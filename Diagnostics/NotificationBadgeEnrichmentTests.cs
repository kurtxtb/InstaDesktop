using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using InstaDesktop.Models;
using InstaDesktop.Services;

namespace InstaDesktop.Diagnostics;

internal static class NotificationBadgeEnrichmentTests
{
    private sealed class Presenter : IWindowsNotificationPresenter
    {
        public event Action<string, bool>? Dismissed { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public List<(InstagramNotification Value, string Token, bool Replace)> Shows { get; } = new();
        public bool Show(InstagramNotification value, string token, string? avatar, string? image, bool replace)
        { Shows.Add((value, token, replace)); return true; }
        public void Remove(string token) { }
    }

    internal static async Task RunAsync(Dispatcher dispatcher, Action<bool, string> check)
    {
        var badge = new InstagramNotification { Source = NotificationSource.UnreadBadge,
            Type = InstagramNotificationType.DirectMessage, StateSequence = "badge:1" };
        var detail = new InstagramNotification { Source = NotificationSource.DirectInbox,
            Title = "Alice", SenderName = "Alice", Body = "Dinner at seven?", ThreadUrl = "https://www.instagram.com/direct/t/7/",
            StateSequence = "inbox:1", Type = InstagramNotificationType.DirectMessage };
        var owned = new List<NotificationService>();
        (NotificationService Service, Presenter Ui) Create(bool monitoring = true)
        {
            var ui = new Presenter();
            var service = new NotificationService(dispatcher, () => true, _ => { }, ui) { DirectInboxMonitoring = monitoring };
            owned.Add(service);
            return (service, ui);
        }
        try
        {
            var forward = Create();
            forward.Service.Receive(badge);
            var reverse = Create();
            reverse.Service.Receive(detail);
            var weak = Create();
            weak.Service.Receive(badge);
            var alone = Create();
            alone.Service.Receive(badge);
            var buttonRow = Create();
            buttonRow.Service.Receive(badge);
            buttonRow.Service.Receive(detail with { ThreadUrl = null, ConversationKey = "row:7" });
            var lateNative = Create();
            lateNative.Service.Receive(new InstagramNotification { Source = NotificationSource.PageNotification });
            lateNative.Service.Receive(detail);
            var weakUnmonitored = Create(monitoring: false);
            weakUnmonitored.Service.Receive(detail with { RequiresNativeConfirmation = true });
            weakUnmonitored.Service.Receive(badge);
            await Task.Delay(150);
            forward.Service.Receive(detail);
            reverse.Service.Receive(badge);
            weak.Service.Receive(detail with { RequiresNativeConfirmation = true });
            await Task.Delay(NotificationService.EnrichmentMilliseconds + 250);
            check(forward.Ui.Shows.Count == 1 && forward.Ui.Shows[0].Value.Title == detail.Title &&
                forward.Ui.Shows[0].Value.Body == detail.Body && forward.Ui.Shows[0].Value.SenderName == detail.SenderName,
                "badge-only signal obtains sender and preview from strong inbox transition");
            check(reverse.Ui.Shows.Count == 1 && reverse.Ui.Shows[0].Value.Body == detail.Body,
                "strong inbox transition preceding badge shares one detailed notification");
            check(weak.Ui.Shows.Count == 1 && weak.Ui.Shows[0].Value.Title == "Instagram",
                "anonymous badge cannot confirm preview-only inbox change");
            check(weakUnmonitored.Ui.Shows.Count == 1 && weakUnmonitored.Ui.Shows[0].Value.Title == "Instagram",
                "anonymous badge cannot confirm preview-only evidence when monitoring unavailable");
            check(alone.Ui.Shows.Count == 1 && alone.Ui.Shows[0].Value.Title == "Instagram",
                "badge without inbox detail still falls back after enrichment window");
            check(buttonRow.Ui.Shows.Count == 1 && buttonRow.Ui.Shows[0].Value.Body == detail.Body &&
                buttonRow.Ui.Shows[0].Value.ThreadUrl is null && buttonRow.Ui.Shows[0].Value.ConversationKey == "row:7",
                "button inbox row enriches sender and preview without inventing a thread URL");

            var dedup = new NotificationDeduplicator();
            var now = DateTimeOffset.UtcNow;
            dedup.Remember(detail with { ThreadUrl = null, ConversationKey = "row:7" }, "first", now);
            check(dedup.Find(detail with { ThreadUrl = null, ConversationKey = "row:8" }, now) is null,
                "two button rows in one snapshot retain separate message identities");

            string token = lateNative.Ui.Shows[0].Token;
            lateNative.Service.Receive(new InstagramNotification { Source = NotificationSource.NativeWebView });
            check(lateNative.Ui.Shows.Count(x => !x.Replace) == 1 && lateNative.Ui.Shows.Last().Token == token &&
                lateNative.Ui.Shows.Last().Value.Source == NotificationSource.NativeWebView &&
                lateNative.Ui.Shows.Last().Value.Title == detail.Title && lateNative.Ui.Shows.Last().Value.Body == detail.Body &&
                lateNative.Ui.Shows.Last().Value.SenderName == detail.SenderName,
                "late generic native event retains enriched page sender and message");
        }
        finally { foreach (var service in owned) service.Dispose(); }
    }
}
