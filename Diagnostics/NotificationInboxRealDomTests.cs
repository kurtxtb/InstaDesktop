using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using InstaDesktop.Models;
using InstaDesktop.Services;

namespace InstaDesktop.Diagnostics;

// Timelines recorded by --inbox-diagnostics on the real inbox (2026-09-29):
// typing makes the row flicker, the badge rises while the row returns, and a
// second unread message turns the preview into "N new messages".
internal static class NotificationInboxRealDomTests
{
    private sealed class Presenter : IWindowsNotificationPresenter
    {
        public event Action<string, bool>? Dismissed { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public List<InstagramNotification> Shows { get; } = new();
        public bool Show(InstagramNotification value, string token, string? avatar, string? image, bool replace)
        { if (!replace) Shows.Add(value); return true; }
        public void Remove(string token) { }
    }

    private sealed record R(string Name, string Preview, bool? Unread, int? Count = null, bool Typing = false);

    internal static async Task RunAsync(Dispatcher dispatcher, Action<bool, string> check)
    {
        string epoch = Guid.NewGuid().ToString();
        int sequence = 0;
        string Snapshot(params R[] rows) => JsonSerializer.Serialize(new
        {
            type = "direct-inbox-snapshot", documentId = epoch, sequence = ++sequence, ready = true,
            threads = rows.Select(r => new { threadUrl = "", rowKey = "name:" + r.Name.ToLowerInvariant(), conversationName = r.Name,
                senderName = "", preview = r.Preview, avatarUrl = "", isUnread = r.Unread, unreadCount = r.Count, outgoing = false, typing = r.Typing })
        });
        var alice = new R("Alice", "Earlier message", false);
        var bob = new R("Bob", "Old message", false);

        var state = new DirectInboxMonitor.InboxState();
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob, alice)).Count == 0, "real inbox baseline is silent");
        var typing = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob, alice with { Preview = "Typing...", Typing = true }));
        check(typing.Count == 0 && !state.UncertainChange, "typing indicator is neither a change nor uncertainty");
        var message = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "Want to grab lunch?", Unread = true }, bob));
        check(message.Count == 1 && message[0].Title == "Alice" && message[0].Body == "Want to grab lunch?" &&
            !message[0].RequiresNativeConfirmation && !state.UncertainChange, "message after typing is strong named evidence");
        var summary = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "2 new messages", Unread = true, Count = 2 }, bob));
        check(summary.Count == 1 && summary[0].Body == "2 new messages" && !summary[0].RequiresNativeConfirmation,
            "second unread message summary is strong evidence");
        var third = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "3 new messages", Unread = true, Count = 3 }, bob));
        check(third.Count == 1 && !third[0].RequiresNativeConfirmation, "rising new-message counter is strong evidence");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "3 new messages", Unread = true, Count = 3 }, bob)).Count == 0,
            "unchanged summary rerender is silent");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "Reacted to a message", Unread = true }, bob)).Single().RequiresNativeConfirmation,
            "other edits of an unread row still need confirmation");

        // A row that React drops for a moment is compared with its last state.
        state = new DirectInboxMonitor.InboxState();
        state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob, alice));
        state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob));
        check(state.UncertainChange, "a disappearing row is still reported as uncertain");
        var returned = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "Back with news", Unread = true }, bob));
        check(returned.Count == 1 && returned[0].Title == "Alice" && !state.UncertainChange,
            "a briefly missing row returning with a message is a change, not a new conversation");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "Back with news", Unread = true }, bob, new R("Carol", "Hello", true))).Count == 0 &&
            state.UncertainChange, "a never-seen row still baselines silently");
        state = new DirectInboxMonitor.InboxState();
        state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob));
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob, new R("Dave", "Typing...", false, Typing: true))).Count == 0,
            "typing on an unknown row waits silently");
        epoch = Guid.NewGuid().ToString();
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob, alice with { Preview = "new document", Unread = true })).Count == 0,
            "a reloaded document never reuses the previous document's rows");

        // End to end: the badge rises in the primary page while the monitor sees
        // the message; the single toast carries the sender and the text.
        var ui = new Presenter();
        using (var service = new NotificationService(dispatcher, () => true, _ => { }, ui) { DirectInboxMonitoring = true })
        {
            epoch = Guid.NewGuid().ToString();
            state = new DirectInboxMonitor.InboxState();
            state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob, alice));
            state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(bob, alice with { Preview = "Typing...", Typing = true }));
            service.Receive(new InstagramNotification { Source = NotificationSource.UnreadBadge, StateSequence = "badge:1",
                Type = InstagramNotificationType.DirectMessage });
            foreach (var candidate in state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "Want to grab lunch?", Unread = true }, bob)))
                service.Receive(candidate);
            if (state.UncertainChange) service.NoteInboxUncertainty();
            await Task.Delay(NotificationService.EnrichmentMilliseconds + 400);
            check(ui.Shows.Count == 1 && ui.Shows[0].Title == "Alice" && ui.Shows[0].Body == "Want to grab lunch?",
                "badge plus real inbox message shows one toast with sender and text");
            // Second message: the badge does not rise for an already-unread thread.
            foreach (var candidate in state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(alice with { Preview = "2 new messages", Unread = true, Count = 2 }, bob)))
                service.Receive(candidate);
            await Task.Delay(NotificationService.EnrichmentMilliseconds + 400);
            check(ui.Shows.Count == 2 && ui.Shows[1].Title == "Alice" && ui.Shows[1].Body == "2 new messages",
                "second message in an unread thread still shows the sender");
        }
    }
}
