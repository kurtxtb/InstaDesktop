using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using InstaDesktop.Models;
using InstaDesktop.Services;

namespace InstaDesktop.Diagnostics;

internal static class NotificationUnknownUnreadTests
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
            Type = InstagramNotificationType.DirectMessage, StateSequence = "unknown-badge:1" };
        var unknown = new InstagramNotification { Source = NotificationSource.DirectInbox,
            Title = "小明", SenderName = "小明", Body = "晚點一起吃飯？ 👋", ConversationKey = "name:小明",
            Type = InstagramNotificationType.DirectMessage, StateSequence = "unknown-inbox:1",
            RequiresNativeConfirmation = true, AllowsBadgeConfirmation = true };
        var owned = new List<NotificationService>();
        (NotificationService Service, Presenter Ui) Create(bool monitoring = true)
        {
            var ui = new Presenter();
            var service = new NotificationService(dispatcher, () => true, _ => { }, ui) { DirectInboxMonitoring = monitoring };
            owned.Add(service);
            return (service, ui);
        }
        bool HasDetail(Presenter ui) => ui.Shows.Count == 1 && ui.Shows[0].Value.Title == unknown.Title &&
            ui.Shows[0].Value.Body == unknown.Body && ui.Shows[0].Value.SenderName == unknown.SenderName &&
            ui.Shows[0].Value.ThreadUrl is null && ui.Shows[0].Value.ConversationKey == unknown.ConversationKey &&
            !ui.Shows[0].Value.RequiresNativeConfirmation;
        bool HasOnlyGeneric(Presenter ui) => ui.Shows.Count == 1 && ui.Shows[0].Value.Title == "Instagram";
        try
        {
            var forward = Create(); forward.Service.Receive(badge);
            var reverse = Create(); reverse.Service.Receive(unknown);
            var alone = Create(); alone.Service.Receive(unknown);
            var ambiguous = Create(); ambiguous.Service.Receive(badge); ambiguous.Service.Receive(unknown);
            ambiguous.Service.Receive(unknown with { Title = "小美", SenderName = "小美", Body = "另一則訊息", ConversationKey = "name:小美" });
            var readEdit = Create(); readEdit.Service.Receive(badge);
            readEdit.Service.Receive(unknown with { AllowsBadgeConfirmation = false, StateSequence = "read-edit:1" });
            var unreadEdit = Create(); unreadEdit.Service.Receive(badge);
            unreadEdit.Service.Receive(unknown with { AllowsBadgeConfirmation = false, StateSequence = "unread-edit:1" });
            var uncertain = Create(); uncertain.Service.Receive(badge); uncertain.Service.Receive(unknown); uncertain.Service.NoteInboxUncertainty();
            var competing = Create(); competing.Service.Receive(badge);
            competing.Service.Receive(badge with { StateSequence = "unknown-badge:2" }); competing.Service.Receive(unknown);
            var unavailable = Create(monitoring: false); unavailable.Service.Receive(unknown); unavailable.Service.Receive(badge);
            var reset = Create(); reset.Service.Receive(badge); reset.Service.Receive(unknown); reset.Service.ResetInboxEnrichment();
            var native = Create(); native.Service.Receive(new InstagramNotification { Source = NotificationSource.NativeWebView }); native.Service.Receive(unknown);
            await Task.Delay(150);
            forward.Service.Receive(unknown);
            // Repeating the same snapshot must neither become a competing change
            // nor produce a second notification after confirmation.
            forward.Service.Receive(unknown);
            reverse.Service.Receive(badge);
            // Wait for observable delivery rather than assuming the dispatcher
            // always services every timer within a fixed 250 ms allowance.
            var waiting = Stopwatch.StartNew();
            while (waiting.ElapsedMilliseconds < NotificationService.EnrichmentMilliseconds + 3000 &&
                (new[] { forward.Ui, reverse.Ui, ambiguous.Ui, readEdit.Ui, unreadEdit.Ui, uncertain.Ui,
                    unavailable.Ui, reset.Ui, native.Ui }.Any(ui => ui.Shows.Count == 0) || competing.Ui.Shows.Count < 2))
                await Task.Delay(25);
            check(HasDetail(forward.Ui), "unknown unread preview following independent badge enriches exact sender and content once");
            check(HasDetail(reverse.Ui), "unknown unread preview preceding independent badge enriches once" +
                (HasDetail(reverse.Ui) ? "" : " (shows=" + reverse.Ui.Shows.Count + ", generic=" +
                    reverse.Ui.Shows.Count(x => x.Value.Title == "Instagram") + ", pending=" +
                    reverse.Ui.Shows.Count(x => x.Value.RequiresNativeConfirmation) + ")"));
            check(alone.Ui.Shows.Count == 0, "unknown unread preview alone cannot produce a notification");
            check(HasOnlyGeneric(ambiguous.Ui), "two unknown unread preview changes keep badge generic without guessing a sender");
            check(HasOnlyGeneric(readEdit.Ui), "explicit-read preview edit remains ineligible for badge confirmation");
            check(HasOnlyGeneric(unreadEdit.Ui), "known-unread preview edit remains ineligible for badge confirmation");
            check(HasOnlyGeneric(uncertain.Ui), "unclassified competing preview change vetoes unknown unread badge attribution");
            check(competing.Ui.Shows.Count == 2 && competing.Ui.Shows.All(x => x.Value.Title == "Instagram"),
                "two independent badge candidates cannot both claim one unknown unread preview");
            check(HasOnlyGeneric(unavailable.Ui), "unavailable inbox monitoring cannot bypass controlled badge attribution");
            check(HasOnlyGeneric(reset.Ui), "monitor reset preserves badge fallback and abandons unconfirmed detail");
            check(HasDetail(native.Ui), "native notification continues to confirm unknown unread preview");

            string token = forward.Ui.Shows.Single().Token;
            forward.Service.Receive(new InstagramNotification { Source = NotificationSource.NativeWebView });
            check(forward.Ui.Shows.Count(x => !x.Replace) == 1 && forward.Ui.Shows.Last().Token == token &&
                forward.Ui.Shows.Last().Value.Title == unknown.Title && forward.Ui.Shows.Last().Value.Body == unknown.Body,
                "late native confirmation retains unknown unread sender and content without another popup");

            // A stale unknown preview must not be attributed to an unrelated
            // badge that starts its observation window much later.
            alone.Service.Receive(badge);
            await Task.Delay(NotificationService.EnrichmentMilliseconds + 200);
            check(HasOnlyGeneric(alone.Ui), "badge outside unknown preview correlation window falls back without stale attribution");
        }
        finally { foreach (var service in owned) service.Dispose(); }
    }
}
