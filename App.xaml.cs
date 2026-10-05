using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using InstaDesktop.Localization;
using InstaDesktop.Services;

namespace InstaDesktop;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private Mutex? _installerMutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsMutex;
    private string? _pendingNotificationActivation;
    internal bool DiagnosticMode { get; private set; }
    internal bool WindowLayoutTest { get; private set; }
    internal bool NotificationTest { get; private set; }
    internal bool MediaPermissionTest { get; private set; }
    internal bool SettingsSnapshot { get; private set; }
    // Real signed-in profile, redacted report only; see InboxDiagnosticsRunner.
    internal bool InboxDiagnostics { get; private set; }
    internal int InboxDiagnosticSeconds { get; private set; } = 180;
    internal string? DiagnosticOutput { get; private set; }
    public SettingsService Settings { get; } = new();
    // Normal mode only; null in diagnostic runs.
    internal UpdateController? Updates { get; private set; }
    private InstanceChannel? _channel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += UnhandledUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LoggingService.Write(LogEvent.UnexpectedException, args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LoggingService.Write(LogEvent.UnexpectedException, args.Exception);
            args.SetObserved();
        };
        Loc.Use(UiLanguage.System);
        try
        {
            int diagnosticIndex = Array.IndexOf(e.Args, "--smoke-test");
            if (diagnosticIndex < 0)
            {
                diagnosticIndex = Array.IndexOf(e.Args, "--window-layout-test");
                WindowLayoutTest = diagnosticIndex >= 0;
            }
            DiagnosticMode = diagnosticIndex >= 0;
            if (!DiagnosticMode)
            {
                diagnosticIndex = Array.IndexOf(e.Args, "--notification-test");
                NotificationTest = DiagnosticMode = diagnosticIndex >= 0;
            }
            if (!DiagnosticMode)
            {
                diagnosticIndex = Array.IndexOf(e.Args, "--media-permission-test");
                MediaPermissionTest = DiagnosticMode = diagnosticIndex >= 0;
            }
            if (!DiagnosticMode)
            {
                diagnosticIndex = Array.IndexOf(e.Args, "--settings-snapshot");
                SettingsSnapshot = DiagnosticMode = diagnosticIndex >= 0;
            }
            if (!DiagnosticMode)
            {
                diagnosticIndex = Array.IndexOf(e.Args, "--inbox-diagnostics");
                InboxDiagnostics = DiagnosticMode = diagnosticIndex >= 0;
                if (InboxDiagnostics && diagnosticIndex + 2 < e.Args.Length && int.TryParse(e.Args[diagnosticIndex + 2], out int seconds))
                    InboxDiagnosticSeconds = Math.Clamp(seconds, 30, 900);
            }
            if (e.Args.Contains("--uninstall-notifications"))
            {
                WindowsNotificationService.Uninstall();
                Shutdown();
                return;
            }
            if (DiagnosticMode)
            {
                if (diagnosticIndex + 1 >= e.Args.Length) { Shutdown(2); return; }
                DiagnosticOutput = Path.GetFullPath(e.Args[diagnosticIndex + 1]);
                // Inbox diagnostics need the signed-in profile, so it must own the
                // single-instance lock instead of sharing the profile with the app.
                if (InboxDiagnostics)
                {
                    if (!AcquireInstance())
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticOutput)!);
                        File.WriteAllText(DiagnosticOutput, "{\"success\":false,\"failure\":\"Close InstaDesktop (including the tray icon) first.\"}");
                        Shutdown(3); return;
                    }
                }
                else AppPaths.UseDiagnosticRoot(Path.Combine(Path.GetDirectoryName(DiagnosticOutput)!,
                    "profile-" + Guid.NewGuid().ToString("N")));
            }
            else if (!AcquireInstance(waitForPrevious: e.Args.Contains(RelaunchArgument), AppCommands.Find(e.Args))) { Shutdown(); return; }
            if (!DiagnosticMode) WindowsNotificationService.Register(arguments =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow is MainWindow ready) ready.Notifications.Activate(arguments);
                    else _pendingNotificationActivation = arguments;
                }));
            });
            LoggingService.Write(LogEvent.AppStarted, code: typeof(App).Assembly.GetName().Version?.Build ?? 0);
            await Settings.LoadAsync();
            // Diagnostics compare English text and the default dark look.
            Loc.Use(DiagnosticMode ? UiLanguage.English : Settings.Current.Language);
            ThemeService.Use(DiagnosticMode ? AppTheme.Dark : Settings.Current.Theme);
            var startCommand = DiagnosticMode ? AppCommand.None : AppCommands.Find(e.Args);
            var window = new MainWindow(Settings, e.Args.Contains("--background") && startCommand == AppCommand.None, startCommand);
            MainWindow = window;
            if (DiagnosticMode) { window.ShowInTaskbar = false; window.Opacity = 0; }
            window.Show();
            if (_pendingNotificationActivation is { } activation)
            { window.Notifications.Activate(activation); _pendingNotificationActivation = null; }
            if (!DiagnosticMode)
            {
                Updates = new UpdateController(window, Settings);
                _ = Updates.StartAsync(background: e.Args.Contains("--background"));
                // Taskbar jump list entries start a second process that hands
                // its command over here.
                _channel = new InstanceChannel();
                _channel.Listen(command => Dispatcher.BeginInvoke(new Action(() => window.RunCommand(command))));
                AppJumpList.Apply(this);
            }
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            if (!DiagnosticMode) MessageBox.Show(Loc.T("App.StartFailed"), "InstaDesktop");
            Shutdown(1);
        }
    }

    // A restart started by the running instance waits for it to exit.
    internal const string RelaunchArgument = "--relaunch";

    // command: a jump list action for an instance that is already running.
    private bool AcquireInstance(bool waitForPrevious = false, AppCommand command = AppCommand.None)
    {
        string suffix = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        _instanceMutex = new Mutex(true, @"Local\InstaDesktop-" + suffix, out _ownsMutex);
        if (!_ownsMutex && waitForPrevious)
        {
            try { _ownsMutex = _instanceMutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
        }
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\InstaDesktop-Activate-" + suffix);
        if (!_ownsMutex)
        {
            if (command == AppCommand.None || !new InstanceChannel().Send(command)) _activation.Set();
            return false;
        }
        _installerMutex = new Mutex(false, @"Local\InstaDesktop-Running");
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.ShowFromTray()));
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    private void UnhandledUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LoggingService.Write(LogEvent.UnexpectedException, e.Exception);
        e.Handled = true;
        if (!DiagnosticMode)
            MessageBox.Show(Loc.T("App.UnexpectedError"), "InstaDesktop");
        Shutdown(1);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.PrepareForShutdown();
        base.OnSessionEnding(e);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Updates?.Stop();
        _channel?.Dispose();
        ThemeService.Stop();
        (MainWindow as MainWindow)?.DisposeResources();
        _activationWait?.Unregister(null);
        _activation?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _installerMutex?.Dispose();
        base.OnExit(e);
    }
}
