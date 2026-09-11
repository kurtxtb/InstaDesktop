using System;
using System.ComponentModel;
using System.Windows;
using InstaDesktop.Models;
using InstaDesktop.Services;

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
            await _web.ResetPermissionsAsync();
            StatusText.Text = "Website permissions reset. Instagram will ask again when needed.";
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            StatusText.Text = "Permission reset is unavailable. Update WebView2 Runtime and try again.";
        }
    }
}
