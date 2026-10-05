using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using InstaDesktop.Localization;
using InstaDesktop.Services;
using Forms = System.Windows.Forms;

namespace InstaDesktop;

public enum UpdateCheckResult { UpToDate, Available, Failed }

// Startup and periodic update checks, the "updated" notice and Settings'
// Check for updates. With automatic updates on, a release found while the app
// runs is installed only while nobody is using the window or a call; with them
// off, the user is told once per version and installs it with a click.
internal sealed class UpdateController
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly MainWindow _window;
    private readonly SettingsService _settings;
    private readonly AutoUpdateService _service = new();
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _lastCheck;
    private AutoUpdateService.UpdateInfo? _pending;
    private string? _downloaded;
    private Version? _announced;
    private bool _busy;

    public UpdateController(MainWindow window, SettingsService settings)
    {
        _window = window;
        _settings = settings;
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(15), DispatcherPriority.Background,
            async (_, _) => await TickAsync(), window.Dispatcher);
        _timer.Stop();
    }

    public static Version CurrentVersion =>
        AutoUpdateService.Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
    public AutoUpdateService.UpdateInfo? Available => _pending;

    public async Task StartAsync(bool background)
    {
        AutoUpdateService.DeleteOldDownloads();
        await AnnounceUpdatedAsync();
        _timer.Start();
        if (!_settings.Current.AutoUpdate) { await CheckAndOfferAsync(); return; }
        _lastCheck = DateTimeOffset.UtcNow;
        try
        {
            // At startup nobody is in a conversation or call yet: install right away.
            if (await _service.TryUpdateAsync(background)) { _timer.Stop(); await _window.ExitAsync(); }
        }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    public void Stop() => _timer.Stop();

    // A different version than last time means this start follows an update.
    private async Task AnnounceUpdatedAsync()
    {
        string current = CurrentVersion.ToString(3);
        string? previous = _settings.Current.LastRunVersion;
        if (previous == current) return;
        if (UpdatedNotice(previous, CurrentVersion) is { } version)
        {
            LoggingService.Write(LogEvent.UpdateNotice, code: 1);
            _window.ShowBalloon(Loc.F("Update.UpdatedTitle", version.ToString(3)), Loc.T("Update.UpdatedText"),
                () => ShellService.OpenWeb(AutoUpdateService.ReleasePage(version)));
        }
        try { await _settings.UpdateAsync(s => s.LastRunVersion = current); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    // null for a first install (nothing ran before) or a downgrade.
    internal static Version? UpdatedNotice(string? previous, Version current) =>
        previous is not null && Version.TryParse(previous, out var last) && AutoUpdateService.IsNewer(current, last) ? current : null;

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!_settings.Current.AutoUpdate) { await CheckAndOfferAsync(); return; }
            if (_pending is null && DateTimeOffset.UtcNow - _lastCheck >= Interval)
            {
                _lastCheck = DateTimeOffset.UtcNow;
                _pending = await _service.CheckAsync();
            }
            if (_pending is not { } update) return;
            if (_downloaded is null || !File.Exists(_downloaded))
            {
                _downloaded = await _service.DownloadAsync(update);
                // A mismatched download is not retried until the next check.
                if (_downloaded is null) { _pending = null; return; }
            }
            // Checked again after the download: the window may have opened meanwhile.
            if (!_window.IsIdleForUpdate) { LoggingService.Write(LogEvent.UpdateDeferred); return; }
            _timer.Stop();
            AutoUpdateService.Launch(_downloaded, background: !_window.IsVisible);
            await _window.ExitAsync();
        }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
        finally { _busy = false; }
    }

    // Automatic updates off: look every few hours and say so once per version.
    private async Task CheckAndOfferAsync()
    {
        if (DateTimeOffset.UtcNow - _lastCheck < Interval) return;
        _lastCheck = DateTimeOffset.UtcNow;
        try { _pending = await _service.CheckAsync(); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); return; }
        if (_pending is not { } update || update.Version == _announced) return;
        _announced = update.Version;
        _window.ShowBalloon(Loc.F("Update.AvailableTitle", update.Version.ToString(3)),
            Loc.T("Update.AvailableText"), () => _ = InstallNowAsync(update));
    }

    // Settings > Check for updates.
    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        _lastCheck = DateTimeOffset.UtcNow;
        try
        {
            _pending = await _service.CheckAsync();
            return _pending is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available;
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            return UpdateCheckResult.Failed;
        }
    }

    // The user asked for it: download, then restart into the new version.
    public async Task<bool> InstallNowAsync(AutoUpdateService.UpdateInfo update)
    {
        if (_busy) return false;
        _busy = true;
        try
        {
            string? installer = _downloaded is { } ready && File.Exists(ready) && _pending == update
                ? ready : await _service.DownloadAsync(update);
            if (installer is null)
            {
                _window.ShowBalloon(Loc.T("Update.VerifyFailedTitle"), Loc.T("Update.TryLater"), null, Forms.ToolTipIcon.Warning);
                return false;
            }
            _timer.Stop();
            AutoUpdateService.Launch(installer, background: !_window.IsVisible);
            await _window.ExitAsync();
            return true;
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            _window.ShowBalloon(Loc.T("Update.DownloadFailedTitle"), Loc.T("Update.CheckConnection"), null, Forms.ToolTipIcon.Warning);
            return false;
        }
        finally { _busy = false; }
    }
}
