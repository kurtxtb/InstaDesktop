using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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
    }

    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _enabled;
    private readonly Action<string?> _activate;
    private readonly NotificationDeduplicator _dedup = new();
    private readonly IWindowsNotificationPresenter _windows;
    private readonly NotificationImageCache _images = new();
    private readonly List<Delivery> _deliveries = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

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
            if (userCanceled && Find(token) is { } d) Close(d, remove: false);
        });
        _windows.Failed += token => Dispatch(() =>
        {
            if (Find(token) is not { } d || d.Closed || !_enabled() || d.TextRetried) return;
            d.TextRetried = true;
            if (_windows.Show(d.Value, d.Token, null, null, replace: false)) ReportShown(d);
        });
    }

    public void Receive(InstagramNotification value, CoreWebView2Notification? native = null)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_enabled()) return;
        var n = NotificationPolicy.Normalize(value);
        LoggingService.Notification(LogEvent.NotificationCandidate, n);
        foreach (var expired in _deliveries.Where(d => d.Created < DateTimeOffset.UtcNow.AddDays(-1)).ToArray())
        { Close(expired, remove: true); _deliveries.Remove(expired); }
        string? match = _dedup.Find(n, DateTimeOffset.UtcNow);
        var delivery = match is null ? null : Find(match);
        if (delivery?.Closed == true) return;
        bool existing = delivery is not null;
        delivery ??= new Delivery { Value = n };
        _dedup.Remember(n, delivery.Token, DateTimeOffset.UtcNow);
        if (native is not null)
        {
            var captured = delivery;
            EventHandler<object> handler = (_, _) => Close(captured, remove: true, reportClosed: false);
            native.CloseRequested += handler;
            delivery.Native.Add(new NativeLifetime { Notification = native, CloseHandler = handler });
        }
        if (existing)
        {
            LoggingService.Notification(LogEvent.NotificationDuplicate, n);
            var preferred = n.Source < delivery.Value.Source ? n : delivery.Value;
            var other = n.Source < delivery.Value.Source ? delivery.Value : n;
            delivery.Value = preferred with
            {
                ThreadUrl = preferred.ThreadUrl ?? other.ThreadUrl,
                AvatarUrl = preferred.AvatarUrl ?? other.AvatarUrl,
                Type = preferred.Type == InstagramNotificationType.DirectMessage ? preferred.Type : other.Type
            };
            if (delivery.Submitted)
            {
                // A late richer source updates the same Action Center item silently.
                _windows.Show(delivery.Value, delivery.Token, delivery.AvatarPath, delivery.ImagePath, replace: true);
                ReportShown(delivery);
            }
            return;
        }
        _deliveries.Add(delivery);
        if (_deliveries.Count > 64)
        { Close(_deliveries[0], remove: true); _deliveries.RemoveAt(0); }
        LoggingService.Notification(LogEvent.NotificationAccepted, n);
        _ = ShowAsync(delivery);
    }

    private async Task ShowAsync(Delivery delivery)
    {
        try
        {
            int delay = delivery.Value.Source switch
            { NotificationSource.NativeWebView => 0, NotificationSource.PageNotification => 750, NotificationSource.DirectDom => 1200, _ => 1800 };
            await Task.Delay(delay, _shutdown.Token);
            if (!CanShow(delivery)) return;
            var avatar = _images.GetAsync(delivery.Value.AvatarUrl, _shutdown.Token);
            var image = _images.GetAsync(delivery.Value.ImageUrl, _shutdown.Token);
            await Task.WhenAll(avatar, image);
            if (!CanShow(delivery)) return;
            delivery.AvatarPath = await avatar;
            delivery.ImagePath = await image;
            delivery.Submitted = _windows.Show(delivery.Value, delivery.Token, delivery.AvatarPath, delivery.ImagePath, replace: false);
            if (!delivery.Submitted && (await avatar is not null || await image is not null))
                delivery.Submitted = _windows.Show(delivery.Value, delivery.Token, null, null, replace: false);
            if (delivery.Submitted) ReportShown(delivery);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { LoggingService.Write(LogEvent.WindowsNotificationFailed, error); }
    }

    private bool CanShow(Delivery d) => !_disposed && !d.Closed && _enabled();
    private Delivery? Find(string token) => _deliveries.FirstOrDefault(d => d.Token == token);
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
            if (Find(token) is { } d)
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
        if (remove) _windows.Remove(d.Token);
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
