using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
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
            StatusText.Text = "Windows startup registration is unavailable on this device.";
        }
        CloseBehaviorCombo.SelectedIndex = (int)s.CloseButton;
        SidebarModeCombo.SelectedIndex = (int)s.SidebarMode;
        CompactLayoutCheck.IsChecked = s.CompactInstagramLayout;
        HardwareCheck.IsChecked = s.HardwareAcceleration;
        DevToolsCheck.IsChecked = s.DeveloperTools;
        CustomizationCheck.IsChecked = s.UiCustomization;
        LowMemoryCheck.IsChecked = s.BackgroundLowMemory;
        AppNotificationsCheck.IsChecked = s.AppNotifications;
        MicrophoneCheck.IsChecked = s.AllowMicrophone;
        CameraCheck.IsChecked = s.AllowCamera;
        if (!web.MemoryApiAvailable) StatusText.Text = "This WebView2 Runtime does not support low-memory mode. Normal mode is used.";
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "InstaDesktop" : $"InstaDesktop {version.Major}.{version.Minor}.{version.Build}";
        SourceInitialized += (_, _) => WindowTheme.UseDarkTitleBar(this);
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
            !on ? "Off: Instagram calls cannot use it."
            : site is null ? ""
            : !wasOn ? "Instagram will ask on the next call."
            : site switch
            {
                CoreWebView2PermissionState.Allow => "Allowed for Instagram.",
                CoreWebView2PermissionState.Deny => "Blocked by your earlier answer. Turn this off and on (Save each time), or reset website permissions, to be asked again.",
                _ => "Instagram will ask on the next call."
            };
        MicrophoneState.Text = Describe(MicrophoneCheck.IsChecked == true, _settings.Current.AllowMicrophone, _microphoneSite);
        CameraState.Text = Describe(CameraCheck.IsChecked == true, _settings.Current.AllowCamera, _cameraSite);
    }

    private void MicrophonePrivacy_Click(object sender, RoutedEventArgs e) => OpenPrivacy(camera: false);
    private void CameraPrivacy_Click(object sender, RoutedEventArgs e) => OpenPrivacy(camera: true);
    private void OpenPrivacy(bool camera)
    {
        if (!ShellService.OpenPrivacySettings(camera))
            StatusText.Text = "Windows Settings could not be opened. Open Settings > Privacy & security > " + (camera ? "Camera." : "Microphone.");
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        var next = _settings.Current.Copy();
        bool gpuChanged = next.HardwareAcceleration != (HardwareCheck.IsChecked == true);
        bool customizationChanged = next.UiCustomization != (CustomizationCheck.IsChecked == true);
        next.StartWithWindows = StartupCheck.IsChecked == true;
        next.CloseButton = (CloseButtonBehavior)CloseBehaviorCombo.SelectedIndex;
        next.SidebarMode = (SidebarMode)Math.Clamp(SidebarModeCombo.SelectedIndex, 0, 1);
        next.CompactInstagramLayout = CompactLayoutCheck.IsChecked == true;
        next.HardwareAcceleration = HardwareCheck.IsChecked == true;
        next.DeveloperTools = DevToolsCheck.IsChecked == true;
        next.UiCustomization = CustomizationCheck.IsChecked == true;
        next.BackgroundLowMemory = LowMemoryCheck.IsChecked == true;
        next.AppNotifications = AppNotificationsCheck.IsChecked == true;
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
            await _web.ApplySettingsAsync(customizationChanged);
            if (gpuChanged)
                MessageBox.Show(this, "Saved. Hardware acceleration changes apply after Tray > Exit and relaunch.", "InstaDesktop");
            _saving = false;
            Close();
        }
        catch (Exception error)
        {
            if (!saved && registryChanged)
            {
                try { StartupService.RestoreRegistration(priorRegistration); }
                catch (Exception rollbackError) { LoggingService.Write(LogEvent.UnexpectedException, rollbackError); }
            }
            LoggingService.Write(LogEvent.UnexpectedException, error);
            StatusText.Text = saved ? "Settings were saved. Exit and relaunch to apply them." : "Settings could not be saved. Check folder permissions and try again.";
        }
        finally { _saving = false; SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) { if (_saving) e.Cancel = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Assets_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Assets);
    private void Logs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Logs);
    private void OpenFolder(string path)
    {
        try { ShellService.OpenFolder(path); }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); StatusText.Text = "The folder could not be opened."; }
    }

    private async void Permissions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = await _web.ResetPermissionsAsync() switch
            {
                PermissionResetResult.Reset => "Website permissions reset. Instagram will ask again when needed; a call in progress loses camera and microphone access.",
                PermissionResetResult.NotReady => "Instagram is not loaded yet. Wait for it to load, then try again.",
                PermissionResetResult.Unsupported => "Permission reset is unavailable. Update WebView2 Runtime and try again.",
                _ => "Website permissions could not be reset. Check the app log and try again."
            };
            await RefreshMediaStateAsync();
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            StatusText.Text = "Website permissions could not be reset. Check the app log and try again.";
        }
    }
}
