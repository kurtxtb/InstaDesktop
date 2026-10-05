using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Linq;
using InstaDesktop.Localization;
using InstaDesktop.Services;
using Forms = System.Windows.Forms;

namespace InstaDesktop;

// Tray icon, its notices and menu, the unread badge (taskbar, tray, title)
// and pausing notifications.
public partial class MainWindow
{
    private Forms.ToolStripMenuItem? _pauseMenu, _resumeItem;
    private Action? _balloonClick;
    private (string Title, string Text, Action? Click, Forms.ToolTipIcon Icon)? _pendingBalloon;
    private IntPtr _badgeIconHandle;
    private Icon? _badgeIcon;
    private DispatcherTimer? _pauseTimer;
    private bool _badgeApplied;
    internal int ShownUnreadCount { get; private set; }

    // The taskbar overlay's ring follows the taskbar color: redraw on a change.
    private void WindowsThemeChanged()
    {
        if (_disposed) return;
        _badgeApplied = false;
        UpdateUnreadBadge();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Tray.Open"), null, (_, _) => ShowFromTray());
        menu.Items.Add(Loc.T("Tray.MessagesWindow"), null, async (_, _) => await RunActionAsync(_web.ToggleMessagesWindowAsync));
        menu.Items.Add(Loc.T("Tray.Refresh"), null, async (_, _) => await RunActionAsync(_web.ReloadAsync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _pauseMenu = new Forms.ToolStripMenuItem(Loc.T("Tray.Pause"));
        _pauseMenu.DropDownItems.Add(Loc.T("Tray.Pause1h"), null, async (_, _) => await PauseNotificationsAsync(DateTimeOffset.Now.AddHours(1)));
        _pauseMenu.DropDownItems.Add(Loc.T("Tray.Pause8h"), null, async (_, _) => await PauseNotificationsAsync(DateTimeOffset.Now.AddHours(8)));
        _pauseMenu.DropDownItems.Add(Loc.T("Tray.PauseTomorrow"), null, async (_, _) => await PauseNotificationsAsync(TomorrowMorning(DateTimeOffset.Now)));
        _pauseMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        _resumeItem = new Forms.ToolStripMenuItem(Loc.T("Tray.Resume"), null, async (_, _) => await PauseNotificationsAsync(null));
        _pauseMenu.DropDownItems.Add(_resumeItem);
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(Loc.T("Tray.Settings"), null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.Exit"), null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => UpdatePauseMenu();
        _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "InstaDesktop", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => { var click = _balloonClick; _balloonClick = null; click?.Invoke(); };
        _tray.BalloonTipClosed += (_, _) => _balloonClick = null;
        UpdateUnreadBadge();
        SchedulePauseEnd();
        if (_pendingBalloon is { } pending) { _pendingBalloon = null; ShowBalloon(pending.Title, pending.Text, pending.Click, pending.Icon); }
    }

    // One tray notice at a time; onClick runs when it is clicked.
    internal void ShowBalloon(string title, string text, Action? onClick, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info, int milliseconds = 6000)
    {
        if (_disposed) return;
        if (_tray is null) { _pendingBalloon = (title, text, onClick, icon); return; }
        _balloonClick = onClick;
        _tray.ShowBalloonTip(milliseconds, title, text, icon);
    }

    // --- Unread badge -------------------------------------------------------

    private void UpdateUnreadBadge()
    {
        if (_disposed) return;
        int count = _settings.Current.UnreadBadge ? _web.UnreadCount : 0;
        // Settings change often (window placement); redraw only for a new count.
        if (_badgeApplied && count == ShownUnreadCount) { UpdateTrayText(); return; }
        _badgeApplied = _tray is not null;
        ShownUnreadCount = count;
        Title = count > 0 ? $"({count}) InstaDesktop" : "InstaDesktop";
        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.Overlay = count > 0 ? CreateOverlay(count, ThemeService.TaskbarUsesLightTheme()) : null;
        TaskbarItemInfo.Description = count > 0 ? UnreadText(count) : "";
        if (_tray is null || _trayIcon is null) return;
        var previousHandle = _badgeIconHandle;
        var previousIcon = _badgeIcon;
        if (count > 0)
        {
            (_badgeIcon, _badgeIconHandle) = CreateTrayBadge(_trayIcon);
            _tray.Icon = _badgeIcon;
        }
        else
        {
            (_badgeIcon, _badgeIconHandle) = (null, IntPtr.Zero);
            _tray.Icon = _trayIcon;
        }
        previousIcon?.Dispose();
        if (previousHandle != IntPtr.Zero) DestroyIcon(previousHandle);
        UpdateTrayText();
    }

    private static string UnreadText(int count) => count == 1 ? Loc.T("Main.UnreadOne") : Loc.F("Main.UnreadMany", count);

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        string text = "InstaDesktop";
        if (ShownUnreadCount > 0) text += " - " + UnreadText(ShownUnreadCount);
        if (_settings.Current.AppNotifications && _settings.Current.NotificationsPausedUntil is { } until && until > DateTimeOffset.Now)
            text += "\n" + Loc.F("Main.PausedUntil", FormatTime(until));
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private static readonly System.Windows.Media.Color BadgeRed = System.Windows.Media.Color.FromRgb(0xFF, 0x30, 0x40);

    // Taskbar overlay (Windows shows it at 16x16 over the icon's corner): a red
    // count bubble inside a ring in the taskbar's own color, so it reads as cut
    // out of the red/pink app icon instead of merging with it.
    internal static ImageSource CreateOverlay(int count, bool lightTaskbar)
    {
        const double size = 32, ring = 3.5;
        var center = new System.Windows.Point(size / 2, size / 2);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var ringColor = lightTaskbar ? System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3) : System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1C);
            dc.DrawEllipse(new SolidColorBrush(ringColor), null, center, size / 2, size / 2);
            dc.DrawEllipse(new SolidColorBrush(BadgeRed), null, center, size / 2 - ring, size / 2 - ring);
            string label = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                label.Length > 1 ? 14 : 20, System.Windows.Media.Brushes.White, 1.0);
            // Center the glyphs themselves, not the line box (which adds space above).
            var glyphs = text.BuildGeometry(new System.Windows.Point(0, 0));
            var bounds = glyphs.Bounds;
            dc.PushTransform(new TranslateTransform(center.X - bounds.X - bounds.Width / 2, center.Y - bounds.Y - bounds.Height / 2));
            dc.DrawGeometry(System.Windows.Media.Brushes.White, null, glyphs);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)size, (int)size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // Tray: the app icon with a red dot (a number would be unreadable at 16 px).
    // The dot sits in a transparent notch cut into the icon, so whatever is
    // behind the tray (dark, light or accent) separates it from the icon.
    internal static (Icon Icon, IntPtr Handle) CreateTrayBadge(Icon source, System.Drawing.Size? requested = null)
    {
        var size = requested ?? Forms.SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var scaled = new Icon(source, size)) g.DrawIcon(scaled, new Rectangle(0, 0, size.Width, size.Height));
            float outer = size.Width * 0.62f, gap = Math.Max(1.5f, size.Width / 10f);
            var notch = new RectangleF(size.Width - outer + gap / 2, -gap / 2, outer, outer);
            var dot = RectangleF.Inflate(notch, -gap, -gap);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(System.Drawing.Color.Transparent)) g.FillEllipse(clear, notch);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            using var fill = new SolidBrush(System.Drawing.Color.FromArgb(BadgeRed.R, BadgeRed.G, BadgeRed.B));
            g.FillEllipse(fill, dot);
        }
        IntPtr handle = bitmap.GetHicon();
        return (System.Drawing.Icon.FromHandle(handle), handle);
    }

    internal void SaveTrayIconForDiagnostics(string path)
    {
        if (_tray?.Icon is not { } icon) return;
        using var bitmap = icon.ToBitmap();
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void DisposeTrayBadge()
    {
        _badgeIcon?.Dispose();
        _badgeIcon = null;
        if (_badgeIconHandle != IntPtr.Zero) DestroyIcon(_badgeIconHandle);
        _badgeIconHandle = IntPtr.Zero;
    }

    // --- New message: flash the taskbar button --------------------------------

    internal int FlashCount { get; private set; }

    // Only while InstaDesktop is open but none of its windows is in front;
    // Windows keeps the button highlighted until the window is activated.
    internal void FlashForNewMessage()
    {
        if (_disposed || !_settings.Current.FlashTaskbar || !IsVisible) return;
        if (Application.Current.Windows.OfType<Window>().Any(w => w.IsActive)) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = new System.Windows.Interop.WindowInteropHelper(this).Handle,
            Flags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG
            Count = 3
        };
        if (info.Window == IntPtr.Zero) return;
        FlashWindowEx(ref info);
        FlashCount++;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    // --- Mute one conversation (a toast's Mute button) -----------------------

    internal static readonly TimeSpan MuteDuration = TimeSpan.FromHours(8);

    internal async Task MuteConversationAsync(string thread)
    {
        if (NotificationPolicy.DirectUrl(thread) is not { } safe) return;
        var until = DateTimeOffset.UtcNow + MuteDuration;
        try
        {
            await _settings.UpdateAsync(s => s.MutedConversations =
                new Dictionary<string, DateTimeOffset>(s.MutedConversations, StringComparer.Ordinal) { [safe] = until });
        }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    // --- Pause notifications ------------------------------------------------

    internal static DateTimeOffset TomorrowMorning(DateTimeOffset now) =>
        new DateTimeOffset(now.Date.AddDays(1).AddHours(8), now.Offset);

    internal static string FormatTime(DateTimeOffset when) =>
        when.LocalDateTime.Date == DateTime.Today ? when.LocalDateTime.ToString("t", CultureInfo.CurrentCulture)
            : when.LocalDateTime.ToString("ddd t", CultureInfo.CurrentCulture);

    internal bool NotificationsActive => _settings.Current.AppNotifications && !_settings.Current.NotificationsPaused(DateTimeOffset.Now);

    // null resumes.
    internal async Task PauseNotificationsAsync(DateTimeOffset? until)
    {
        LoggingService.Write(LogEvent.NotificationsPaused, code: until is { } end ? (int)Math.Ceiling((end - DateTimeOffset.Now).TotalMinutes) : 0);
        try { await _settings.UpdateAsync(s => s.NotificationsPausedUntil = until); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
        SchedulePauseEnd();
        UpdateTrayText();
    }

    private void UpdatePauseMenu()
    {
        if (_pauseMenu is null || _resumeItem is null) return;
        bool enabled = _settings.Current.AppNotifications;
        bool paused = enabled && _settings.Current.NotificationsPaused(DateTimeOffset.Now);
        _pauseMenu.Enabled = enabled;
        _pauseMenu.Text = !enabled ? Loc.T("Tray.PauseOff")
            : paused ? Loc.F("Main.PausedUntil", FormatTime(_settings.Current.NotificationsPausedUntil!.Value))
            : Loc.T("Tray.Pause");
        _resumeItem.Enabled = paused;
    }

    // Refreshes the tray text when a pause ends and forgets the old time.
    private void SchedulePauseEnd()
    {
        _pauseTimer?.Stop();
        if (_settings.Current.NotificationsPausedUntil is not { } until) return;
        var remaining = until - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) { _ = PauseNotificationsAsync(null); return; }
        _pauseTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _pauseTimer.Tick -= PauseEnded;
        _pauseTimer.Tick += PauseEnded;
        _pauseTimer.Interval = remaining < TimeSpan.FromDays(2) ? remaining : TimeSpan.FromDays(2);
        _pauseTimer.Start();
    }

    private void PauseEnded(object? sender, EventArgs e)
    {
        _pauseTimer?.Stop();
        if (_settings.Current.NotificationsPaused(DateTimeOffset.Now)) SchedulePauseEnd();
        else _ = PauseNotificationsAsync(null);
    }
}
