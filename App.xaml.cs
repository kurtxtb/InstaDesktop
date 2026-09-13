using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using InstaDesktop.Services;

namespace InstaDesktop;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private Mutex? _installerMutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsMutex;
    internal bool DiagnosticMode { get; private set; }
    internal bool WindowLayoutTest { get; private set; }
    internal string? DiagnosticOutput { get; private set; }
    public SettingsService Settings { get; } = new();

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
        try
        {
            int diagnosticIndex = Array.IndexOf(e.Args, "--smoke-test");
            if (diagnosticIndex < 0)
            {
                diagnosticIndex = Array.IndexOf(e.Args, "--window-layout-test");
                WindowLayoutTest = diagnosticIndex >= 0;
            }
            DiagnosticMode = diagnosticIndex >= 0;
            if (DiagnosticMode)
            {
                if (diagnosticIndex + 1 >= e.Args.Length) { Shutdown(2); return; }
                DiagnosticOutput = Path.GetFullPath(e.Args[diagnosticIndex + 1]);
                AppPaths.UseDiagnosticRoot(Path.Combine(Path.GetDirectoryName(DiagnosticOutput)!,
                    "profile-" + Guid.NewGuid().ToString("N")));
            }
            else if (!AcquireInstance()) { Shutdown(); return; }
            LoggingService.Write(LogEvent.AppStarted, code: typeof(App).Assembly.GetName().Version?.Build ?? 0);
            await Settings.LoadAsync();
            var window = new MainWindow(Settings, e.Args.Contains("--background"));
            MainWindow = window;
            if (DiagnosticMode) { window.ShowInTaskbar = false; window.Opacity = 0; }
            window.Show();
            _ = CheckForUpdateAsync();
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.UnexpectedException, error);
            if (!DiagnosticMode) MessageBox.Show("InstaDesktop could not start. Check the app log.", "InstaDesktop");
            Shutdown(1);
        }
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            if (await new AutoUpdateService().TryUpdateAsync()) _ = Dispatcher.BeginInvoke(new Action(Shutdown));
        }
        catch (Exception error) { LoggingService.Write(LogEvent.UnexpectedException, error); }
    }

    private bool AcquireInstance()
    {
        string suffix = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        _instanceMutex = new Mutex(true, @"Local\InstaDesktop-" + suffix, out _ownsMutex);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\InstaDesktop-Activate-" + suffix);
        if (!_ownsMutex) { _activation.Set(); return false; }
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
            MessageBox.Show("An unexpected error occurred. InstaDesktop will exit. Your web profile is retained.", "InstaDesktop");
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
        (MainWindow as MainWindow)?.DisposeResources();
        _activationWait?.Unregister(null);
        _activation?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _installerMutex?.Dispose();
        base.OnExit(e);
    }
}
