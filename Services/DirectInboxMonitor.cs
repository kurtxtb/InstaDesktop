using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using InstaDesktop.Models;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop.Services;

// An inbox-only controller, never a second notification presenter. The supplied
// environment/profile belongs to the primary WebView; no credentials are copied.
internal sealed class DirectInboxMonitor : IDisposable
{
    internal const string InboxUrl = "https://www.instagram.com/direct/inbox/";
    internal const string Emitter = "monitor";
    private static int s_live;
    private readonly Dispatcher _dispatcher;
    private readonly NotificationService _notifications;
    private readonly Func<bool> _notificationsEnabled;
    private readonly InboxState _state = new();
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _disposed;
    private bool _scanning;
    internal CoreWebView2? Core => _controller?.CoreWebView2;
    internal bool IsRunning => !_disposed && _controller is not null;
    // Diagnostics: controllers created and not yet closed, across all instances.
    internal static int LiveCount => s_live;
    internal bool HandlersAttached => _core is not null;

    internal async Task RefreshSnapshotAsync()
    {
        if (_disposed || _scanning || Core is not { } core || !IsInbox(core.Source)) return;
        _scanning = true;
        try { await core.ExecuteScriptAsync("window.__InstaDesktopInboxMonitor?.scanNow();").WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception error) { LoggingService.Write(LogEvent.DirectMonitorInitializationFailed, error); }
        finally { _scanning = false; }
    }

    public DirectInboxMonitor(Dispatcher dispatcher, NotificationService notifications, Func<bool> notificationsEnabled)
    { _dispatcher = dispatcher; _notifications = notifications; _notificationsEnabled = notificationsEnabled; }

    internal static bool IsInbox(string? value) => NotificationPolicy.IsInstagramOrigin(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host == "www.instagram.com" &&
        uri.AbsolutePath.TrimEnd('/') == "/direct/inbox" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        !value!.Contains('%') && !value.Contains('\\') && !value.Contains("/../", StringComparison.Ordinal);

    public async Task StartAsync(CoreWebView2 primary, IntPtr parent, Action<CoreWebView2>? diagnosticConfigure = null)
    {
        _dispatcher.VerifyAccess();
        try
        {
            var environment = primary.Environment;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = primary.Profile.ProfileName;
            options.IsInPrivateModeEnabled = primary.Profile.IsInPrivateModeEnabled;
            // Do not timeout/abandon the creation task: a controller completing
            // after disposal still needs to be closed, not leaked.
            var controller = await environment.CreateCoreWebView2ControllerAsync(parent, options);
            if (_disposed) { controller.Close(); return; }
            _controller = controller;
            System.Threading.Interlocked.Increment(ref s_live);
            controller.IsVisible = false;
            controller.Bounds = new Rectangle(0, 0, 1000, 800);
            var core = controller.CoreWebView2;
            core.IsMuted = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            Attach(core);
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; }
            catch (Exception error) { LoggingService.Write(LogEvent.DirectMonitorInitializationFailed, error); }
            // Fail closed: without the capture block the monitor does not start.
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(MediaPermissionPolicy.MonitorCaptureBlockScript); }
            catch (Exception error) { LoggingService.Write(LogEvent.MonitorCaptureBlockFailed, error); throw; }
            if (_disposed) return;
            diagnosticConfigure?.Invoke(core);
            string script = await WebViewService.ReadNotificationAssetAsync("direct-inbox-dom.js") + "\n" +
                await WebViewService.ReadNotificationAssetAsync("direct-inbox-monitor.js");
            await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            if (_disposed) return;
            // Record the shared profile's state the background page will see.
            try { await NotificationPermissionPolicy.LogStateAsync(core.Profile); }
            catch (Exception error) { LoggingService.Write(LogEvent.DirectMonitorInitializationFailed, error); }
            if (_disposed) return;
            core.Navigate(InboxUrl);
            LoggingService.Write(LogEvent.DirectMonitorStarted);
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.DirectMonitorInitializationFailed, error);
            Dispose();
        }
    }

    private void Attach(CoreWebView2 core)
    {
        _core = core;
        core.NotificationReceived += NotificationReceived;
        core.PermissionRequested += PermissionRequested;
        core.NewWindowRequested += NewWindowRequested;
        core.FrameNavigationStarting += FrameNavigationStarting;
        core.DownloadStarting += DownloadStarting;
        core.NavigationStarting += NavigationStarting;
        core.SourceChanged += SourceChanged;
        core.NavigationCompleted += NavigationCompleted;
        core.ProcessFailed += ProcessFailed;
        core.WebMessageReceived += MessageReceived;
    }

    private void Detach()
    {
        if (_core is not { } core) return;
        _core = null;
        try
        {
            core.NotificationReceived -= NotificationReceived;
            core.PermissionRequested -= PermissionRequested;
            core.NewWindowRequested -= NewWindowRequested;
            core.FrameNavigationStarting -= FrameNavigationStarting;
            core.DownloadStarting -= DownloadStarting;
            core.NavigationStarting -= NavigationStarting;
            core.SourceChanged -= SourceChanged;
            core.NavigationCompleted -= NavigationCompleted;
            core.ProcessFailed -= ProcessFailed;
            core.WebMessageReceived -= MessageReceived;
        }
        // A crashed/closed controller may reject removal; Close() still releases it.
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    // The inbox can receive the only genuine native event while the primary
    // page is on another route. Suppress browser UI, then use the existing
    // coordinator and lifecycle handling to deliver it.
    private void NotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        e.Handled = true;
        if (_disposed || Core is not { } core || !IsInbox(core.Source)) return;
        LoggingService.Write(LogEvent.NotificationReceived, code: 2);
        WebViewService.ForwardNativeNotification(_notifications, e, Emitter, this);
    }

    // Instagram may ask for notification permission from this background page.
    // It must follow the AppNotifications setting without any dialog; all other
    // permissions (camera, microphone, ...) and other origins stay denied.
    // A grant already saved for the primary page never raises this event here,
    // so capture is additionally removed by MonitorCaptureBlockScript.
    private void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        var decision = NotificationPermissionPolicy.ForMonitor(e.PermissionKind, e.Uri, !_disposed && _notificationsEnabled());
        e.State = decision.State;
        try { e.SavesInProfile = decision.SavesInProfile; }
        catch (Exception error) when (error is NotImplementedException or System.Runtime.InteropServices.COMException) { }
        LoggingService.Write(LogEvent.DirectMonitorPermission, code: (int)e.PermissionKind * 10 + (int)decision.State);
        if (MediaPermissionPolicy.IsMediaKind(e.PermissionKind))
            LoggingService.Write(LogEvent.MediaPermission, code: MediaPermissionPolicy.LogCode(2, e.PermissionKind, e.Uri,
                MediaPermissionPolicy.ForMonitor(e.PermissionKind, e.Uri).Reason, decision.State, decision.SavesInProfile));
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;
    private void FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) => e.Cancel = true;
    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsInbox(e.Uri)) { e.Cancel = true; StopAfterNavigationReset(); }
        else { _state.Reset(); _notifications.ResetInboxEnrichment(); }
    }

    private void SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    { if (Core is { } core && !IsInbox(core.Source)) StopAfterNavigationReset(); }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) StopAfterNavigationReset();
        else LoggingService.Write(LogEvent.DirectMonitorReady);
    }

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        LoggingService.Write(LogEvent.WebViewProcessError, code: 1000 + (int)e.ProcessFailedKind);
        StopAfterNavigationReset();
    }

    private void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || !IsInbox(e.Source) || Core is not { } core || !IsInbox(core.Source)) return;
        try
        {
            string json = e.WebMessageAsJson;
            if (json.Length < 128)
            {
                using var message = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
                if (message.RootElement.GetProperty("type").GetString() == "direct-monitor-blocked") { StopAfterNavigationReset(); return; }
                if (message.RootElement.GetProperty("type").GetString() == "direct-monitor-extraction-failed")
                { LoggingService.Write(LogEvent.DirectMonitorExtractionFailed); return; }
            }
            var changes = _state.Apply(e.Source, json);
            if (!_state.Ready) _notifications.ResetInboxEnrichment();
            if (_state.UncertainChange) _notifications.NoteInboxUncertainty();
            foreach (var candidate in changes)
            {
                LoggingService.Notification(LogEvent.DirectCandidateCreated, candidate);
                _notifications.Receive(candidate);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
        { _state.Reset(); _notifications.ResetInboxEnrichment(); LoggingService.Write(LogEvent.NotificationRejected, error); }
    }

    private void StopAfterNavigationReset()
    {
        if (_disposed) return;
        _state.Reset();
        _notifications.ResetInboxEnrichment();
        LoggingService.Write(LogEvent.DirectMonitorNavigationReset);
        // Closing a controller from inside its navigation callback is reentrant.
        _dispatcher.BeginInvoke(new Action(Dispose));
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _state.Reset(); _notifications.ResetInboxEnrichment();
        // Detach first so a restarted monitor can never share a stale handler,
        // and release native notifications while this controller is still open.
        Detach();
        _notifications.ReleaseNative(this);
        var controller = _controller; _controller = null;
        if (controller is not null) System.Threading.Interlocked.Decrement(ref s_live);
        try { controller?.Close(); }
        catch (Exception error) { LoggingService.Write(LogEvent.DirectMonitorInitializationFailed, error); }
        LoggingService.Write(LogEvent.DirectMonitorStopped);
    }

    // Snapshot state, not a second deduplication cache. Only rows present in the
    // immediately previous snapshot are eligible for a strong transition.
    internal sealed class InboxState
    {
        private sealed record Row(InstagramNotification Value, bool? Unread, int? Count, bool Outgoing);
        private Dictionary<string, Row>? _previous;
        // Rows seen recently in this document. React briefly drops and re-adds a
        // row (typing indicator, reordering); its return is compared with its last
        // real state instead of being treated as a newly discovered conversation.
        private readonly Dictionary<string, (Row Row, DateTimeOffset Seen)> _recent = new(StringComparer.Ordinal);
        internal static TimeSpan RecentRowLifetime = TimeSpan.FromMinutes(2);
        private string? _document;
        private long _sequence;
        private int _extractionCode = -1;
        private int _unreadStateCode = -1;
        public bool Ready => _previous is not null;
        public bool UncertainChange { get; private set; }
        public void Reset() { _previous = null; _recent.Clear(); _document = null; _sequence = 0; _extractionCode = _unreadStateCode = -1; UncertainChange = false; }

        private Row? Known(string key, string epoch, DateTimeOffset now)
        {
            if (_document != epoch) return null;
            if (_previous is not null && _previous.TryGetValue(key, out var row)) return row;
            return _recent.TryGetValue(key, out var recent) && now - recent.Seen <= RecentRowLifetime ? recent.Row : null;
        }

        public IReadOnlyList<InstagramNotification> Apply(string origin, string json)
        {
            UncertainChange = false;
            if (!IsInbox(origin) || json.Length > 262144) throw new JsonException();
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (Read(root, "type", 40) != "direct-inbox-snapshot") throw new JsonException();
            string epoch = Read(root, "documentId", 64);
            if (!Guid.TryParse(epoch, out _) || !root.GetProperty("sequence").TryGetInt64(out long sequence) || sequence <= 0) throw new JsonException();
            if (root.GetProperty("ready").ValueKind == JsonValueKind.False) { Reset(); return Array.Empty<InstagramNotification>(); }
            if (root.GetProperty("ready").ValueKind != JsonValueKind.True) throw new JsonException();
            var now = DateTimeOffset.UtcNow;
            var threads = root.GetProperty("threads");
            if (threads.ValueKind != JsonValueKind.Array || threads.GetArrayLength() > 40) throw new JsonException();
            var current = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (var item in threads.EnumerateArray())
            {
                string rawUrl = Read(item, "threadUrl", 256);
                string? url = rawUrl.Length == 0 ? null : NotificationPolicy.DirectUrl(rawUrl) ?? throw new JsonException();
                string? key = item.TryGetProperty("rowKey", out _) ? NotificationPolicy.RowKey(Read(item, "rowKey", 240)) : null;
                if (item.TryGetProperty("rowKey", out _) && key is null) throw new JsonException();
                if (url is null && key is null) throw new JsonException();
                string title = Read(item, "conversationName", 160), preview = Read(item, "preview", 1000);
                string sender = Read(item, "senderName", 160), avatar = Read(item, "avatarUrl", 4096);
                bool? unread = Boolean(item.GetProperty("isUnread"));
                bool outgoing = Boolean(item.GetProperty("outgoing")) ?? throw new JsonException();
                var countValue = item.GetProperty("unreadCount");
                int? count = countValue.ValueKind == JsonValueKind.Null ? null : countValue.GetInt32();
                if (count is < 0 or > 9999 || (count is not null && unread is not null && unread != (count > 0))) throw new JsonException();
                bool typing = item.TryGetProperty("typing", out var typingValue) && typingValue.ValueKind == JsonValueKind.True;
                if (typing)
                {
                    // Keep the last real state; an unknown row simply waits.
                    if (Known(url ?? key!, epoch, now) is { } known && !current.TryAdd(url ?? key!, known)) throw new JsonException();
                    continue;
                }
                if (preview.Length > 0 && sender.Length > 0 && sender != title && !preview.StartsWith(sender + ":", StringComparison.Ordinal)) preview = sender + ": " + preview;
                var value = NotificationPolicy.Normalize(new InstagramNotification
                {
                    Source = NotificationSource.DirectInbox, Type = InstagramNotificationType.DirectMessage,
                    Title = title, Body = preview, SenderName = sender, AvatarUrl = avatar,
                    ThreadUrl = url, ConversationKey = key, StateSequence = epoch + ":" + sequence
                });
                if (!current.TryAdd(url ?? key!, new(value, unread, count, outgoing))) throw new JsonException();
            }
            if (_document == epoch && sequence <= _sequence) return Array.Empty<InstagramNotification>();
            int named = 0, previews = 0, unreadRows = 0, readRows = 0, unknownRows = 0;
            foreach (var row in current.Values)
            {
                if (row.Value.Title != "Instagram") named++;
                if (row.Value.Body != "You have a new message") previews++;
                if (row.Unread == true) unreadRows++;
                else if (row.Unread == false) readRows++;
                else unknownRows++;
            }
            // Counts only: diagnose unsupported markup without logging private text.
            int extraction = current.Count * 1000000 + named * 10000 + previews * 100 + unreadRows;
            if (extraction != _extractionCode)
            { LoggingService.Write(LogEvent.DirectMonitorExtraction, code: extraction); _extractionCode = extraction; }
            int unreadState = unreadRows * 10000 + readRows * 100 + unknownRows;
            if (unreadState != _unreadStateCode)
            { LoggingService.Write(LogEvent.DirectMonitorUnreadState, code: unreadState); _unreadStateCode = unreadState; }
            var changes = new List<InstagramNotification>();
            if (_previous is not null && _document == epoch)
            {
                // A disappearing conversation can hide the actual incoming
                // change while another visible preview is merely edited.
                foreach (var (key, old) in _previous)
                    if (!old.Outgoing && !current.ContainsKey(key)) UncertainChange = true;
                foreach (var (url, row) in current)
                {
                    if (row.Outgoing) continue;
                    var old = Known(url, epoch, now);
                    // A briefly missing row is compared with its last state only
                    // when its unread markup is readable; otherwise it baselines
                    // again like a virtualized row, without fabricated detail.
                    bool recalled = old is not null && !_previous.ContainsKey(url);
                    // Newly loaded rows may be historical or genuinely incoming.
                    // They cannot provide a detail candidate, but they can compete
                    // with an unrelated edit for an anonymous badge's identity.
                    if (old is null || (recalled && row.Unread is null)) { UncertainChange = true; continue; }
                    bool previewChanged = old is not null && old.Value.Body != row.Value.Body;
                    if (row.Unread is null)
                    {
                        if (previewChanged && row.Value.Body != "You have a new message" && old!.Value.Body != "You have a new message")
                        {
                            // Missing unread markup is not an explicit read state.
                            // Keep the observed name/body, but never display solely
                            // because text changed. Badge confirmation is confined
                            // to the coordinator's complete unambiguous window.
                            changes.Add(row.Value with
                            {
                                RequiresNativeConfirmation = true,
                                AllowsBadgeConfirmation = old.Unread is null && !old.Outgoing &&
                                    old.Count is null && row.Count is null
                            });
                        }
                        else UncertainChange |= previewChanged;
                        continue;
                    }
                    if (row.Unread == false) { UncertainChange |= previewChanged; continue; }
                    // An already-unread row whose preview becomes an "N new messages"
                    // summary (N >= 2) received another message.
                    bool strong = old is not null && !old.Outgoing &&
                        ((old.Unread != true && previewChanged) || (old.Count is not null && row.Count > old.Count) ||
                         (old.Unread == true && old.Count is null && row.Count >= 2 && previewChanged));
                    // Newly discovered/virtualized rows always baseline silently.
                    // Preview-only changes require a nearby native confirmation.
                    if (strong || previewChanged)
                        changes.Add(row.Value with { RequiresNativeConfirmation = !strong });
                    else if (old!.Unread != true) UncertainChange = true;
                }
            }
            else LoggingService.Write(LogEvent.DirectMonitorBaselineCreated, code: current.Count);
            if (_document != epoch) _recent.Clear();
            foreach (var (key, row) in current) _recent[key] = (row, now);
            foreach (var stale in _recent.Where(x => now - x.Value.Seen > RecentRowLifetime).Select(x => x.Key).ToArray()) _recent.Remove(stale);
            _previous = current; _document = epoch; _sequence = sequence;
            if (changes.Count > 0) LoggingService.Write(LogEvent.DirectSnapshotChanged, code: changes.Count);
            if (UncertainChange) LoggingService.Write(LogEvent.DirectMonitorUncertainChange);
            return changes;
        }

        private static bool? Boolean(JsonElement value) => value.ValueKind switch
        { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null, _ => throw new JsonException() };
        private static string Read(JsonElement value, string name, int max)
        {
            var field = value.GetProperty(name);
            if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text || text.Length > max) throw new JsonException();
            return text;
        }
    }
}
