using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using InstaDesktop.Localization;
using InstaDesktop.Models;
using InstaDesktop.Services;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly WebViewService _web;
    private bool _saving;
    private bool _startupAvailable = true;

    public SettingsWindow(SettingsService settings, WebViewService web)
    {
        InitializeComponent();
        _settings = settings;
        _web = web;
        var s = settings.Current;
        try { StartupCheck.IsChecked = StartupService.IsEnabled(); }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            StartupCheck.IsEnabled = _startupAvailable = false;
            StatusText.Text = Loc.T("Settings.StartupUnavailable");
        }
        CloseBehaviorCombo.SelectedIndex = (int)s.CloseButton;
        OpenInCombo.SelectedIndex = (int)s.OpenNotificationsIn;
        RememberPageCheck.IsChecked = s.RememberLastPage;
        _hotkeyShow = s.HotkeyShowWindow;
        _hotkeyMessages = s.HotkeyMessages;
        ShowHotkeys();
        ThemeCombo.SelectedIndex = (int)s.Theme;
        LanguageCombo.SelectedIndex = (int)s.Language;
        HardwareCheck.IsChecked = s.HardwareAcceleration;
        DevToolsCheck.IsChecked = s.DeveloperTools;
        LowMemoryCheck.IsChecked = s.BackgroundLowMemory;
        AppNotificationsCheck.IsChecked = s.AppNotifications;
        NotificationSoundCheck.IsChecked = s.NotificationSound;
        HideContentCheck.IsChecked = s.HideNotificationContent;
        FlashCheck.IsChecked = s.FlashTaskbar;
        UnreadBadgeCheck.IsChecked = s.UnreadBadge;
        CallOnTopCheck.IsChecked = s.CallWindowsOnTop;
        AskDownloadCheck.IsChecked = s.AskDownloadLocation;
        AutoUpdateCheck.IsChecked = s.AutoUpdate;
        _downloadFolder = s.DownloadFolder;
        ShowDownloadFolder();
        ShowMuted();
        MicrophoneCheck.IsChecked = s.AllowMicrophone;
        CameraCheck.IsChecked = s.AllowCamera;
        if (!web.MemoryApiAvailable) StatusText.Text = Loc.T("Settings.LowMemoryUnsupported");
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "InstaDesktop" : $"InstaDesktop {version.Major}.{version.Minor}.{version.Build}";
        WindowTheme.Track(this);
        Loaded += async (_, _) => await RefreshMediaStateAsync();
    }

    private CoreWebView2PermissionState? _microphoneSite, _cameraSite;

    private async Task RefreshMediaStateAsync()
    {
        _microphoneSite = await _web.GetMediaSiteStateAsync(CoreWebView2PermissionKind.Microphone);
        _cameraSite = await _web.GetMediaSiteStateAsync(CoreWebView2PermissionKind.Camera);
        ShowMediaState();
    }

    private void MediaCheck_Changed(object sender, RoutedEventArgs e) => ShowMediaState();

    // Describes what the next call will do. The profile state shown is the one
    // saved before this window's changes are applied.
    private void ShowMediaState()
    {
        if (MicrophoneState is null || CameraState is null) return;
        string Describe(bool on, bool wasOn, CoreWebView2PermissionState? site) =>
            !on ? Loc.T("Media.Off")
            : site is null ? ""
            : !wasOn ? Loc.T("Media.AskNext")
            : site switch
            {
                CoreWebView2PermissionState.Allow => Loc.T("Media.Allowed"),
                CoreWebView2PermissionState.Deny => Loc.T("Media.Blocked"),
                _ => Loc.T("Media.AskNext")
            };
        MicrophoneState.Text = Describe(MicrophoneCheck.IsChecked == true, _settings.Current.AllowMicrophone, _microphoneSite);
        CameraState.Text = Describe(CameraCheck.IsChecked == true, _settings.Current.AllowCamera, _cameraSite);
    }

    private void MicrophonePrivacy_Click(object sender, RoutedEventArgs e) => OpenPrivacy(camera: false);
    private void CameraPrivacy_Click(object sender, RoutedEventArgs e) => OpenPrivacy(camera: true);
    private void OpenPrivacy(bool camera)
    {
        if (!ShellService.OpenPrivacySettings(camera))
            StatusText.Text = Loc.T(camera ? "Settings.PrivacyFailedCamera" : "Settings.PrivacyFailedMicrophone");
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        var next = _settings.Current.Copy();
        // The WebView2 browser reads GPU use only when it starts; the language
        // is read once at startup.
        bool restartNeeded = next.HardwareAcceleration != (HardwareCheck.IsChecked == true) ||
            next.Language != (UiLanguage)Math.Clamp(LanguageCombo.SelectedIndex, 0, 2);
        next.StartWithWindows = StartupCheck.IsChecked == true;
        next.CloseButton = (CloseButtonBehavior)CloseBehaviorCombo.SelectedIndex;
        next.OpenNotificationsIn = (NotificationOpenTarget)Math.Clamp(OpenInCombo.SelectedIndex, 0, 1);
        next.RememberLastPage = RememberPageCheck.IsChecked == true;
        if (!next.RememberLastPage) next.LastPage = null;
        next.HotkeyShowWindow = _hotkeyShow;
        next.HotkeyMessages = _hotkeyMessages;
        next.Theme = (AppTheme)Math.Clamp(ThemeCombo.SelectedIndex, 0, 2);
        next.Language = (UiLanguage)Math.Clamp(LanguageCombo.SelectedIndex, 0, 2);
        next.HardwareAcceleration = HardwareCheck.IsChecked == true;
        next.DeveloperTools = DevToolsCheck.IsChecked == true;
        next.BackgroundLowMemory = LowMemoryCheck.IsChecked == true;
        next.AppNotifications = AppNotificationsCheck.IsChecked == true;
        next.NotificationSound = NotificationSoundCheck.IsChecked == true;
        next.HideNotificationContent = HideContentCheck.IsChecked == true;
        next.FlashTaskbar = FlashCheck.IsChecked == true;
        next.UnreadBadge = UnreadBadgeCheck.IsChecked == true;
        next.CallWindowsOnTop = CallOnTopCheck.IsChecked == true;
        next.AskDownloadLocation = AskDownloadCheck.IsChecked == true;
        next.DownloadFolder = _downloadFolder;
        next.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        next.AllowMicrophone = MicrophoneCheck.IsChecked == true;
        next.AllowCamera = CameraCheck.IsChecked == true;
        string? priorRegistration = null;
        bool registryChanged = false;
        bool saved = false;
        try
        {
            if (_startupAvailable)
            {
                priorRegistration = StartupService.ReadRegistration();
                string? desiredRegistration = next.StartWithWindows ? StartupService.Command : null;
                if (!string.Equals(priorRegistration, desiredRegistration, StringComparison.OrdinalIgnoreCase))
                {
                    StartupService.SetEnabled(next.StartWithWindows);
                    registryChanged = true;
                }
            }
            await _settings.SaveAsync(next);
            saved = true;
            ThemeService.Use(next.Theme);
            await _web.ApplySettingsAsync();
            _saving = false;
            Close();
            if (restartNeeded && Owner is MainWindow main && MessageBox.Show(main, Loc.T("Settings.RestartQuestion"), "InstaDesktop",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                await main.RestartAsync();
        }
        catch (Exception error)
        {
            if (!saved && registryChanged)
            {
                try { StartupService.RestoreRegistration(priorRegistration); }
                catch (Exception rollbackError) { LoggingService.Write(LogEvent.UnexpectedException, rollbackError); }
            }
            LoggingService.Write(LogEvent.UnexpectedException, error);
            StatusText.Text = Loc.T(saved ? "Settings.SavedRelaunch" : "Settings.SaveFailed");
        }
        finally { _saving = false; SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) { if (_saving) e.Cancel = true; }

    // ---- System-wide shortcuts: press the keys in the box to set them -----------

    private string _hotkeyShow = "", _hotkeyMessages = "";

    private void ShowHotkeys()
    {
        HotkeyShowBox.Text = string.IsNullOrEmpty(_hotkeyShow) ? Loc.T("Shortcuts.Off") : _hotkeyShow;
        HotkeyMessagesBox.Text = string.IsNullOrEmpty(_hotkeyMessages) ? Loc.T("Shortcuts.Off") : _hotkeyMessages;
        var conflicts = (Owner as MainWindow)?.HotkeyConflicts;
        string State(string value, string saved, int id) =>
            value != saved ? Loc.T("Shortcuts.SaveToApply")
            : conflicts?.Contains(id) == true && !string.IsNullOrEmpty(value) ? Loc.T("Shortcuts.InUse")
            : Loc.T("Shortcuts.Hint");
        HotkeyShowState.Text = State(_hotkeyShow, _settings.Current.HotkeyShowWindow, MainWindow.HotkeyShowWindowId);
        HotkeyMessagesState.Text = State(_hotkeyMessages, _settings.Current.HotkeyMessages, MainWindow.HotkeyMessagesId);
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        // Tab still moves between controls.
        if (key == System.Windows.Input.Key.Tab && (modifiers & ~System.Windows.Input.ModifierKeys.Shift) == 0) return;
        e.Handled = true;
        string? value = null;
        if (modifiers == System.Windows.Input.ModifierKeys.None && key is System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete)
            value = "";
        else if (!Hotkey.IsModifier(key) && new Hotkey(modifiers, key) is { IsValid: true } hotkey)
            value = hotkey.ToString();
        if (value is null) return; // a modifier alone, or a combination without Ctrl/Alt/Win
        if ((string)box.Tag == "show") _hotkeyShow = value; else _hotkeyMessages = value;
        ShowHotkeys();
    }

    private void ViewShortcuts_Click(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.ShowShortcuts();

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Adv.ExportTitle"), FileName = DiagnosticsExport.DefaultFileName,
            Filter = Loc.T("Adv.ExportFilter"), DefaultExt = ".zip",
            InitialDirectory = _settings.Current.DownloadFolder ?? ShellService.DownloadsFolder
        };
        if (dialog.ShowDialog(this) != true) return;
        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            await DiagnosticsExport.ExportAsync(dialog.FileName, _settings.Current);
            StatusText.Text = Loc.T("Adv.Exported");
            ShellService.ShowInFolder(dialog.FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            StatusText.Text = Loc.T("Adv.ExportFailed");
        }
        finally { ExportDiagnosticsButton.IsEnabled = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Logs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Logs);

    // Muted conversations change from toasts at any time; this only shows the
    // count and clears them all at once (immediately, like Reset permissions).
    private void ShowMuted()
    {
        int count = _settings.Current.MutedConversations.Count(m => m.Value > DateTimeOffset.UtcNow);
        MutedText.Text = count == 0 ? Loc.T("Notif.MutedNone") : Loc.F("Notif.MutedSome", count);
        UnmuteAllButton.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void UnmuteAll_Click(object sender, RoutedEventArgs e)
    {
        try { await _settings.UpdateAsync(s => s.MutedConversations = new Dictionary<string, DateTimeOffset>()); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
        ShowMuted();
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        StatusText.Text = await _web.ClearCacheAsync() switch
        {
            true => Loc.T("Adv.CacheCleared"),
            null => Loc.T("Perm.NotReady"),
            false => Loc.T("Adv.CacheFailed")
        };
        ClearCacheButton.IsEnabled = true;
    }

    // null = the Windows Downloads folder. Applied on Save, like every other setting.
    private string? _downloadFolder;

    private void ShowDownloadFolder()
    {
        DownloadFolderText.Text = _downloadFolder ?? Loc.F("Downloads.WindowsDownloads", ShellService.DownloadsFolder);
        ResetDownloadFolderButton.Visibility = _downloadFolder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChangeDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Downloads.ChooseTitle"),
            InitialDirectory = _downloadFolder ?? ShellService.DownloadsFolder
        };
        if (dialog.ShowDialog(this) != true) return;
        _downloadFolder = dialog.FolderName;
        ShowDownloadFolder();
    }

    private void ResetDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        _downloadFolder = null;
        ShowDownloadFolder();
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_downloadFolder ?? ShellService.DownloadsFolder);

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (((App)Application.Current).Updates is not { } updates)
        {
            UpdateStatusText.Text = Loc.T("Updates.NotAvailable");
            return;
        }
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = Loc.T("Updates.Checking");
        var result = await updates.CheckNowAsync();
        CheckUpdatesButton.IsEnabled = true;
        string current = UpdateController.CurrentVersion.ToString(3);
        if (result == UpdateCheckResult.Failed)
        {
            UpdateStatusText.Text = Loc.T("Updates.CheckFailed");
            return;
        }
        if (result == UpdateCheckResult.UpToDate || updates.Available is not { } update)
        {
            UpdateStatusText.Text = Loc.F("Updates.UpToDate", current);
            return;
        }
        string version = update.Version.ToString(3);
        UpdateStatusText.Text = Loc.F("Updates.Available", version, current);
        if (MessageBox.Show(this, Loc.F("Updates.InstallQuestion", version), Loc.T("Updates.DialogTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        UpdateStatusText.Text = Loc.T("Updates.Downloading");
        CheckUpdatesButton.IsEnabled = false;
        if (!await updates.InstallNowAsync(update))
        {
            UpdateStatusText.Text = Loc.T("Updates.InstallFailed");
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OpenFolder(string path)
    {
        try { ShellService.OpenFolder(path); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); StatusText.Text = Loc.T("Settings.FolderOpenFailed"); }
    }

    private async void Permissions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = Loc.T(await _web.ResetPermissionsAsync() switch
            {
                PermissionResetResult.Reset => "Perm.Reset",
                PermissionResetResult.NotReady => "Perm.NotReady",
                PermissionResetResult.Unsupported => "Perm.Unsupported",
                _ => "Perm.Failed"
            });
            await RefreshMediaStateAsync();
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            StatusText.Text = Loc.T("Perm.Failed");
        }
    }
}
