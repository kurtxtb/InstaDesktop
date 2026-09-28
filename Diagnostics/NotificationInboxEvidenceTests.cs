using System;
using System.Linq;
using System.Text.Json;
using InstaDesktop.Services;

namespace InstaDesktop.Diagnostics;

internal static class NotificationInboxEvidenceTests
{
    internal static void Run(Action<bool, string> check)
    {
        string epoch = Guid.NewGuid().ToString();
        object Row(string name, string preview, bool? unread = null, int? count = null, bool outgoing = false) => new
        {
            threadUrl = "", rowKey = "name:" + name.ToLowerInvariant(), conversationName = name,
            senderName = name, preview, avatarUrl = "", isUnread = unread, unreadCount = count, outgoing
        };
        string Snapshot(int sequence, params object[] rows) => JsonSerializer.Serialize(new
        { type = "direct-inbox-snapshot", documentId = epoch, sequence, ready = true, threads = rows });
        var state = new DirectInboxMonitor.InboxState();
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(1, Row("Alice", "Earlier message"))).Count == 0,
            "unknown unread baseline is silent");
        var delta = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(2, Row("Alice", "Dinner at seven?")));
        check(delta.Count == 1 && delta[0].RequiresNativeConfirmation && delta[0].AllowsBadgeConfirmation &&
            delta[0].Title == "Alice" && delta[0].Body == "Dinner at seven?" && !state.UncertainChange,
            "unknown unread preview delta retains sender and body pending independent confirmation");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(3, Row("Alice", "Dinner at seven?"))).Count == 0,
            "unknown unread unchanged preview remains silent");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(4, Row("Alice", "You: fine", outgoing: true))).Count == 0,
            "unknown unread outgoing preview remains silent");
        delta = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(5, Row("Alice", "Changed after own reply")));
        check(delta.Count == 1 && delta[0].RequiresNativeConfirmation && !delta[0].AllowsBadgeConfirmation,
            "transition from outgoing preview requires native confirmation");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(6, Row("Alice", "Changed but explicitly read", false, 0))).Count == 0 && state.UncertainChange,
            "explicit read state vetoes unknown badge attribution");
        state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(7, Row("Alice", "Known unread", true, 2)));
        delta = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(8, Row("Alice", "Edited unread", true, 2)));
        check(delta.Single().RequiresNativeConfirmation && !delta[0].AllowsBadgeConfirmation,
            "unchanged known unread counter cannot authorize badge confirmation of edit");
        state.Reset();
        state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(9, Row("Alice", "Earlier"), Row("Bob", "Earlier")));
        delta = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(10, Row("Alice", "Changed A"), Row("Bob", "Changed B")));
        check(delta.Count == 2 && delta.All(value => value.RequiresNativeConfirmation && value.AllowsBadgeConfirmation),
            "simultaneous unknown preview changes remain separate competing candidates");
        delta = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(11, Row("Alice", "Changed again"), Row("Bob", "Changed B"), Row("Chen", "Newly loaded")));
        check(delta.Count == 1 && state.UncertainChange,
            "newly discovered competing row prevents attributing badge to an unrelated preview edit");
        delta = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(12, Row("Bob", "Possible edit")));
        check(delta.Count == 1 && state.UncertainChange,
            "disappearing competing row prevents attributing badge to remaining preview edit");
        check(state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(13, Row("Alice", "Reappeared"), Row("Bob", "Possible edit"))).Count == 0,
            "virtualized unknown unread row is baselined again without fabricated detail");
        state.Reset();
        state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(14, Row("Alice", "Earlier"), Row("Bob", "OK", false)));
        delta = state.Apply(DirectInboxMonitor.InboxUrl, Snapshot(15, Row("Alice", "Edited"), Row("Bob", "OK", true)));
        check(delta.Count == 1 && state.UncertainChange,
            "another row becoming unread with identical text vetoes unrelated preview attribution");
    }
}
