using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using InstaDesktop.Localization;
using InstaDesktop.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace InstaDesktop.Services;

// All state and WebView lifecycle calls belong to the WPF dispatcher. Only image
// downloads/decoding and Windows event callbacks originate on worker threads.
public sealed class NotificationService : IDisposable
{
    private sealed class NativeLifetime
    {
        public required CoreWebView2Notification Notification;
        public required EventHandler<object> CloseHandler;
        // The controller that raised it. Its native objects become invalid
        // (access violation, not an exception) once that controller closes.
        public object? Owner;
        public bool Shown;
    }
    private sealed class Delivery
    {
        public string Token { get; } = Guid.NewGuid().ToString("N")[..16];
        public required InstagramNotification Value;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public List<NativeLifetime> Native { get; } = new();
        public bool Submitted, Closed, TextRetried;
        public string? AvatarPath, ImagePath;
        public bool AwaitingEnrichment, WasGeneric, InboxEvidence;
        // Grouping: a later message of the same conversation reuses the toast
        // of the one before it (same Windows tag), so Action Center keeps one
        // entry per conversation. Token stays this delivery's own identity.
        public string? GroupToken;
        public string ToastToken => GroupToken ?? Token;
        public int GroupCount = 1;
    }

    // How long a conversation's toast keeps collecting new messages.
    internal static TimeSpan GroupWindow { get; set; } = TimeSpan.FromHours(1);

    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _enabled;
    private readonly Action<string?> _activate;
    private readonly NotificationDeduplicator _dedup = new();
    private readonly IWindowsNotificationPresenter _windows;
    private readonly NotificationImageCache _images = new();
    private readonly List<Delivery> _deliveries = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;
    private DateTimeOffset _uncertainInboxChange = DateTimeOffset.MinValue;
    internal bool DirectInboxMonitoring { get; set; }
    // The user's presentation choices. Off: every toast is shown without its sound.
    internal Func<bool> SoundEnabled { get; set; } = () => true;
    // On: the toast names the sender only (no message text, no image).
    internal Func<bool> HideContent { get; set; } = () => false;
    // A muted Direct thread never shows a toast (checked when it would appear).
    internal Func<string, bool> IsMuted { get; set; } = _ => false;
    // A toast's "Mute" button: the validated thread URL to mute.
    internal event Action<string>? MuteRequested;
    // A new toast appeared (not an update of one already shown).
    internal event Action? Presented;

    private bool Muted(Delivery d) => d.Value.ThreadUrl is { } thread && IsMuted(thread);

    private bool Present(Delivery d, string? avatar, string? image, bool replace, bool announce = true)
    {
        var n = d.Value;
        if (!SoundEnabled()) n = n with { Silent = true };
        if (n.Body == GenericBody) n = n with { Body = Loc.T("Notify.GenericMessage") };
        if (HideContent())
        {
            bool message = n.Type == InstagramNotificationType.DirectMessage || n.ThreadUrl is not null;
            n = n with { Body = Loc.T(message ? "Notify.HiddenMessage" : "Notify.HiddenActivity"), ImageUrl = null };
            image = null;
        }
        if (d.GroupCount > 1)
        {
            n = n with { GroupedCount = d.GroupCount };
            if (HideContent()) n = n with { Body = Loc.F("Notify.HiddenMessages", d.GroupCount) };
        }
        bool shown = _windows.Show(n, d.ToastToken, avatar, image, replace);
        if (shown && !replace && announce) Presented?.Invoke();
        return shown;
    }

    // The placeholder body NotificationPolicy uses when Instagram gave none.
    private const string GenericBody = "You have a new message";
    internal const int EnrichmentMilliseconds = 2000;

    public NotificationService(Dispatcher dispatcher, Func<bool> enabled, Action<string?> activate)
        : this(dispatcher, enabled, activate, new WindowsNotificationService()) { }

    internal NotificationService(Dispatcher dispatcher, Func<bool> enabled, Action<string?> activate, IWindowsNotificationPresenter windows)
    {
        _dispatcher = dispatcher; _enabled = enabled; _activate = activate;
        _windows = windows;
        _windows.Dismissed += (token, userCanceled) => Dispatch(() =>
        {
            // Timeout dismisses only the banner; the notification is still clickable
            // in Notification Center. Retain its native lifecycle until user removal.
            if (userCanceled && FindToast(token) is { } d) Close(d, remove: false);
        });
        _windows.Failed += token => Dispatch(() =>
        {
            if (FindToast(token) is not { } d || d.Closed || !_enabled() || d.TextRetried) return;
            d.TextRetried = true;
            if (Present(d, null, null, replace: false, announce: false)) ReportShown(d);
        });
    }

    public void Receive(InstagramNotification value, CoreWebView2Notification? native = null, object? nativeOwner = null)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_enabled()) return;
        var n = NotificationPolicy.Normalize(value);
        LoggingService.Notification(LogEvent.NotificationCandidate, n);
        foreach (var expired in _deliveries.Where(d => d.Created < DateTimeOffset.UtcNow.AddDays(-1)).ToArray())
        { Close(expired, remove: true); _deliveries.Remove(expired); }
        string? match = _dedup.Find(n, DateTimeOffset.UtcNow, allowAnonymousCorrelation: !DirectInboxMonitoring);
        var delivery = match is null ? null : Find(match);
        if (delivery?.Closed == true) return;
        bool existing = delivery is not null;
        delivery ??= new Delivery { Value = n };
        delivery.InboxEvidence |= n.Source == NotificationSource.DirectInbox ||
            (DirectInboxMonitoring && n.Source == NotificationSource.DirectDom &&
                (n.ThreadUrl is not null || !string.IsNullOrEmpty(n.ConversationKey)));
        if (!delivery.Submitted && !delivery.WasGeneric && DirectInboxMonitoring &&
            (n.Source <= NotificationSource.PageNotification || n.Source == NotificationSource.UnreadBadge) &&
            NotificationPolicy.CanEnrich(n))
        {
            delivery.AwaitingEnrichment = delivery.WasGeneric = true;
            LoggingService.Write(LogEvent.NativeCandidateAwaitingEnrichment);
        }
        _dedup.Remember(n, delivery.Token, DateTimeOffset.UtcNow);
        if (native is not null)
        {
            var captured = delivery;
            EventHandler<object> handler = (_, _) => Close(captured, remove: true, reportClosed: false);
            native.CloseRequested += handler;
            delivery.Native.Add(new NativeLifetime { Notification = native, CloseHandler = handler, Owner = nativeOwner });
        }
        if (existing)
        {
            LoggingService.Notification(LogEvent.NotificationDuplicate, n);
            bool wasAwaitingConfirmation = delivery.Value.RequiresNativeConfirmation;
            var before = delivery.Value;
            var preferred = n.Source < delivery.Value.Source ? n : delivery.Value;
            var other = n.Source < delivery.Value.Source ? delivery.Value : n;
            // Source priority decides delivery behavior, but a later generic
            // native/page event must not erase the sender and preview we learned.
            delivery.Value = preferred with
            {
                Title = string.Equals(preferred.Title, "Instagram", StringComparison.OrdinalIgnoreCase) ? other.Title : preferred.Title,
                Body = HasGenericBody(preferred) ? other.Body : preferred.Body,
                SenderName = string.IsNullOrEmpty(preferred.SenderName) ? other.SenderName : preferred.SenderName,
                ThreadUrl = preferred.ThreadUrl ?? other.ThreadUrl,
                ConversationKey = preferred.ConversationKey ?? other.ConversationKey,
                AvatarUrl = preferred.AvatarUrl ?? other.AvatarUrl,
                ImageUrl = preferred.ImageUrl ?? other.ImageUrl,
                Type = preferred.Type == InstagramNotificationType.DirectMessage ? preferred.Type : other.Type,
                RequiresNativeConfirmation = (preferred.RequiresNativeConfirmation || other.RequiresNativeConfirmation) &&
                    !ConfirmsMessage(preferred) && !ConfirmsMessage(other),
                AllowsBadgeConfirmation = (preferred.AllowsBadgeConfirmation || other.AllowsBadgeConfirmation) &&
                    !(preferred.RequiresNativeConfirmation && !preferred.AllowsBadgeConfirmation) &&
                    !(other.RequiresNativeConfirmation && !other.AllowsBadgeConfirmation)
            };
            if (delivery.Submitted)
            {
                // A late richer source updates the same Action Center item silently.
                // A mirrored event adding nothing only joins the native lifecycle.
                if (Muted(delivery))
                {
                    // Enrichment revealed a muted conversation: take the toast back.
                    Close(delivery, remove: true);
                    return;
                }
                if (delivery.Value != before)
                    Present(delivery, delivery.AvatarPath, delivery.ImagePath, replace: true);
                ReportShown(delivery);
            }
            else if (n.Source == NotificationSource.NativeWebView && !NotificationPolicy.CanEnrich(n))
            {
                // Rich native content must not inherit an earlier inbox wait.
                delivery.AwaitingEnrichment = delivery.InboxEvidence = false;
                _ = ShowAsync(delivery, immediate: true);
            }
            else if (wasAwaitingConfirmation && !delivery.Value.RequiresNativeConfirmation)
            {
                // Preview-only evidence may have finished its original wait without
                // showing. A later page notification or strong DOM event must restart
                // delivery; CanShow still prevents any overlapping wait from repeating it.
                _ = ShowAsync(delivery);
            }
            return;
        }
        _deliveries.Add(delivery);
        if (_deliveries.Count > 64)
        { Close(_deliveries[0], remove: true); _deliveries.RemoveAt(0); }
        LoggingService.Notification(LogEvent.NotificationAccepted, n);
        _ = ShowAsync(delivery);
    }

    private async Task ShowAsync(Delivery delivery, bool immediate = false)
    {
        try
        {
            int delay = immediate ? 0 : delivery.AwaitingEnrichment || delivery.InboxEvidence ? EnrichmentMilliseconds : delivery.Value.Source switch
            { NotificationSource.NativeWebView => 0, NotificationSource.PageNotification => 750, NotificationSource.DirectDom => 1200, _ => 1800 };
            await Task.Delay(delay, _shutdown.Token);
            if (!CanShow(delivery)) return;
            if (delivery.AwaitingEnrichment)
            {
                double remaining = EnrichmentMilliseconds - (DateTimeOffset.UtcNow - delivery.Created).TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining) + 1), _shutdown.Token);
            }
            if (delivery.InboxEvidence)
            {
                // If the inbox led the native event, let that event finish its
                // own observation window before showing a separate DOM popup.
                double remaining = _deliveries.Where(d => d.AwaitingEnrichment && Nearby(d, delivery))
                    .Select(d => EnrichmentMilliseconds - (DateTimeOffset.UtcNow - d.Created).TotalMilliseconds)
                    .DefaultIfEmpty(0).Max();
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining) + 1), _shutdown.Token);
            }
            foreach (var generic in _deliveries.Where(d => d.AwaitingEnrichment &&
                DateTimeOffset.UtcNow - d.Created >= TimeSpan.FromMilliseconds(EnrichmentMilliseconds)).ToArray()) ResolveEnrichment(generic);
            if (!CanShow(delivery) || delivery.Value.RequiresNativeConfirmation) return;
            if (Muted(delivery))
            {
                LoggingService.Write(LogEvent.NotificationMuted);
                Close(delivery, remove: false);
                return;
            }
            var avatar = _images.GetAsync(delivery.Value.AvatarUrl, _shutdown.Token);
            var image = _images.GetAsync(delivery.Value.ImageUrl, _shutdown.Token);
            await Task.WhenAll(avatar, image);
            if (!CanShow(delivery)) return;
            delivery.AvatarPath = await avatar;
            delivery.ImagePath = await image;
            if (GroupFor(delivery) is { } group)
            {
                // Replace that conversation's toast: it pops up again with this
                // message and how many came before it.
                delivery.GroupToken = group.ToastToken;
                delivery.GroupCount = group.GroupCount + 1;
                Close(group, remove: false);
            }
            delivery.Submitted = Present(delivery, delivery.AvatarPath, delivery.ImagePath, replace: false);
            if (!delivery.Submitted && (await avatar is not null || await image is not null))
                delivery.Submitted = Present(delivery, null, null, replace: false);
            if (delivery.Submitted) ReportShown(delivery);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { LoggingService.Write(LogEvent.WindowsNotificationFailed, error); }
    }

    private bool CanShow(Delivery d) => !_disposed && !d.Closed && !d.Submitted && _enabled();
    private static bool HasGenericBody(InstagramNotification n) => NotificationPolicy.CanEnrich(n with
    { Title = "Instagram", SenderName = null, ThreadUrl = null, ConversationKey = null, AvatarUrl = null, ImageUrl = null });
    private static bool ConfirmsMessage(InstagramNotification n) =>
        !n.RequiresNativeConfirmation && n.Source != NotificationSource.UnreadBadge;
    private static bool Nearby(Delivery a, Delivery b) =>
        Math.Abs((a.Created - b.Created).TotalMilliseconds) <= EnrichmentMilliseconds;

    private void ResolveEnrichment(Delivery generic)
    {
        generic.AwaitingEnrichment = false;
        if (generic.Closed) return;
        var changes = _deliveries.Where(d => d.InboxEvidence && !d.Closed && Nearby(d, generic)).ToArray();
        if (Math.Abs((generic.Created - _uncertainInboxChange).TotalMilliseconds) <= EnrichmentMilliseconds ||
            changes.Length != 1 || changes[0].Submitted ||
            _deliveries.Count(d => d.WasGeneric && !d.Closed && Nearby(d, changes[0])) != 1)
        {
            LoggingService.Write(changes.Length == 0 ? LogEvent.EnrichmentTimedOut : LogEvent.EnrichmentAmbiguous);
            return;
        }
        var detail = changes[0];
        // Unknown unread markup can still provide a named preview. Only the
        // unique nearby change may join an independently detected badge rise;
        // known-read/known-unread edits retain their native confirmation rule.
        if (detail.Value.RequiresNativeConfirmation && generic.Value.Source > NotificationSource.PageNotification &&
            !(generic.Value.Source == NotificationSource.UnreadBadge && detail.Value.AllowsBadgeConfirmation))
        { LoggingService.Write(LogEvent.EnrichmentTimedOut); return; }
        if (detail.Value.ThreadUrl is null && string.IsNullOrEmpty(detail.Value.ConversationKey))
        { LoggingService.Write(LogEvent.EnrichmentTimedOut); return; }
        generic.Value = generic.Value with
        {
            Title = detail.Value.Title == "Instagram" ? generic.Value.Title : detail.Value.Title,
            Body = HasGenericBody(detail.Value) ? generic.Value.Body : detail.Value.Body,
            SenderName = detail.Value.SenderName,
            ThreadUrl = detail.Value.ThreadUrl, AvatarUrl = detail.Value.AvatarUrl,
            ConversationKey = detail.Value.ConversationKey,
            Type = InstagramNotificationType.DirectMessage,
            RequiresNativeConfirmation = false,
            AllowsBadgeConfirmation = false
        };
        // No second cache or presentation path: both source records now refer to
        // the original delivery token, including its native lifecycle callbacks.
        detail.Closed = true;
        _dedup.MergeDelivery(detail.Token, generic.Token);
        _dedup.Remember(generic.Value, generic.Token, DateTimeOffset.UtcNow);
        LoggingService.Notification(LogEvent.NativeCandidateEnriched, generic.Value);
    }

    internal void ResetInboxEnrichment()
    {
        // Preserve the original generic candidate and its deadline on monitor failure.
        foreach (var delivery in _deliveries.Where(d => d.InboxEvidence))
        {
            // The primary WebView's own Direct evidence survives monitor failure.
            if (delivery.Value.Source == NotificationSource.DirectDom) continue;
            delivery.InboxEvidence = false;
            if (!delivery.Submitted && delivery.Value.Source == NotificationSource.DirectInbox)
            {
                delivery.Closed = true;
                // This unshown monitor candidate was abandoned, not dismissed by the
                // user. Remove its correlation records so a valid native/page event
                // arriving during the remaining dedup window can still be delivered.
                _dedup.ForgetDelivery(delivery.Token);
            }
        }
    }

    internal void NoteInboxUncertainty() => _uncertainInboxChange = DateTimeOffset.UtcNow;

    // Must run while the owner's controller is still open: detach our handlers
    // and forget its native objects so no later click, dismissal, expiry or
    // reset calls into a closed WebView. The Windows toast itself is retained.
    internal void ReleaseNative(object owner)
    {
        _dispatcher.VerifyAccess();
        foreach (var delivery in _deliveries)
            foreach (var native in delivery.Native.Where(n => ReferenceEquals(n.Owner, owner)).ToArray())
            {
                TryNative(() => native.Notification.CloseRequested -= native.CloseHandler);
                delivery.Native.Remove(native);
            }
    }

    internal int NativeCount(object owner) => _deliveries.Sum(d => d.Native.Count(n => ReferenceEquals(n.Owner, owner)));
    private Delivery? Find(string token) => _deliveries.FirstOrDefault(d => d.Token == token);
    // A Windows toast tag: the newest open delivery shown in it.
    private Delivery? FindToast(string token) =>
        _deliveries.Where(d => d.ToastToken == token).OrderBy(d => d.Closed).ThenByDescending(d => d.Created).FirstOrDefault();

    // The open toast of the same conversation this message joins, if any.
    private Delivery? GroupFor(Delivery delivery)
    {
        if (delivery.Value.ThreadUrl is not { } thread) return null;
        var now = DateTimeOffset.UtcNow;
        return _deliveries.LastOrDefault(d => d != delivery && d.Submitted && !d.Closed &&
            d.Value.ThreadUrl == thread && now - d.Created <= GroupWindow);
    }
    private static bool TryNative(Action action)
    {
        try { action(); return true; }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationLifecycleFailed, error,
            code: action.Method.Name == "ReportShown" ? 1 : action.Method.Name == "ReportClicked" ? 2 : action.Method.Name == "ReportClosed" ? 3 : 4); return false; }
    }
    private static void ReportShown(Delivery d)
    {
        foreach (var native in d.Native.Where(n => !n.Shown))
        { native.Shown = TryNative(native.Notification.ReportShown); }
    }

    public void Activate(string arguments)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || arguments.Length > 2048) return;
        try
        {
            var args = ToastArguments.Parse(arguments);
            if (!args.TryGetValue("notification", out var token) || token.Length != 16 || !token.All(Uri.IsHexDigit)) return;
            string? thread = args.TryGetValue("thread", out var path) ? NotificationPolicy.DirectUrl(path) : null;
            if (args.TryGetValue("action", out var action) && action == "mute")
            {
                // The toast's Mute button: never opens the window.
                var muted = FindToast(token);
                thread = muted?.Value.ThreadUrl ?? thread;
                if (muted is not null) Close(muted, remove: true);
                if (thread is not null) MuteRequested?.Invoke(thread);
                LoggingService.Write(LogEvent.NotificationMuted, code: thread is null ? 0 : 1);
                return;
            }
            if (FindToast(token) is { } d)
            {
                thread = d.Value.ThreadUrl ?? thread;
                foreach (var native in d.Native.Where(n => n.Shown).ToArray()) TryNative(native.Notification.ReportClicked);
                // ReportClicked is terminal in WebView2. A subsequent ReportClosed
                // is rejected; report dismissal only for a separate close action.
                Close(d, remove: true, reportClosed: false);
            }
            LoggingService.Write(LogEvent.NotificationActivated);
            _activate(thread);
        }
        catch (Exception error) { LoggingService.Write(LogEvent.NotificationActivationFailed, error); }
    }

    private void Close(Delivery d, bool remove, bool reportClosed = true)
    {
        if (d.Closed) return;
        d.Closed = true;
        // A grouped toast still showing a newer message belongs to that one.
        if (remove && !_deliveries.Any(o => o != d && !o.Closed && o.ToastToken == d.ToastToken)) _windows.Remove(d.ToastToken);
        foreach (var native in d.Native)
        {
            TryNative(() => native.Notification.CloseRequested -= native.CloseHandler);
            if (native.Shown && reportClosed) TryNative(native.Notification.ReportClosed);
        }
        d.Native.Clear();
    }

    public void Reset(bool removeNotifications)
    {
        foreach (var d in _deliveries) Close(d, removeNotifications);
        _deliveries.Clear(); _dedup.Clear();
    }

    private void Dispatch(Action action)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _shutdown.Cancel();
        Reset(removeNotifications: false); _images.Dispose(); _shutdown.Dispose();
    }
}
