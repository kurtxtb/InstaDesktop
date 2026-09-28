using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using InstaDesktop.Services;

namespace InstaDesktop.Diagnostics;

// --inbox-diagnostics <report> [seconds]: runs the real hidden inbox monitor on
// the user's signed-in profile (nothing is changed or saved) and records a
// redacted structure/transition report. No names or message text are written:
// see Assets/Scripts/inbox-diagnostics.js for exactly what is collected.
internal static class InboxDiagnosticsRunner
{
    public static async Task RunAsync(MainWindow window, SettingsService settings, string output, int seconds)
    {
        var samples = new List<JsonElement>();
        string? failure = null;
        var started = DateTimeOffset.UtcNow;
        var logLines = new List<string>();
        var previous = LoggingService.DiagnosticObserver;
        LoggingService.DiagnosticObserver = (name, error, code) =>
        {
            previous?.Invoke(name, error, code);
            lock (logLines) logLines.Add($"{(DateTimeOffset.UtcNow - started).TotalMilliseconds:0} {name}" +
                (error is null ? "" : " " + error.GetType().Name) + (code is null ? "" : " Code=" + code));
        };
        try
        {
            string script = await WebViewService.ReadNotificationAssetAsync("inbox-diagnostics.js");
            window.Web.DiagnosticMonitorConfigure = core => _ = core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            // In memory only: the monitor runs exactly as with notifications on.
            settings.Current.AppNotifications = true;
            await window.Web.InitializeAsync(_ => { });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (window.Web.DirectMonitor?.IsRunning != true && DateTime.UtcNow < deadline)
            {
                window.Web.ExpireDirectMonitorBackoff();
                await window.Web.RefreshDirectMonitorAsync();
                await Task.Delay(1000);
            }
            if (window.Web.DirectMonitor?.IsRunning != true)
            {
                failure = "Hidden inbox did not start. Open InstaDesktop normally and confirm you are signed in.";
                return;
            }
            var end = DateTime.UtcNow.AddSeconds(seconds);
            var nextStructure = DateTime.MinValue;
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(2000);
                if (window.Web.DirectMonitor?.Core is not { } core) continue;
                bool structure = DateTime.UtcNow >= nextStructure;
                try
                {
                    string raw = await core.ExecuteScriptAsync("window.__InstaDesktopInboxDiagnostics?.take(" + (structure ? "true" : "false") + ") ?? null")
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    if (raw == "null") continue;
                    using var outer = JsonDocument.Parse(raw);
                    using var inner = JsonDocument.Parse(outer.RootElement.GetString()!);
                    samples.Add(inner.RootElement.Clone());
                    if (structure) nextStructure = DateTime.UtcNow.AddSeconds(20);
                }
                catch (Exception error) when (error is TimeoutException or JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                { LoggingService.Write(LogEvent.DirectMonitorInitializationFailed, error); }
            }
        }
        catch (Exception error)
        {
            failure = error.GetType().Name;
            LoggingService.Write(LogEvent.UnexpectedException, error);
        }
        finally
        {
            LoggingService.DiagnosticObserver = previous;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string[] log; lock (logLines) log = logLines.ToArray();
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                success = failure is null, failure, started, seconds,
                runtime = window.Web.Core?.Environment.BrowserVersionString,
                log, samples
            }, new JsonSerializerOptions { WriteIndented = true }));
            window.PrepareForShutdown(); window.DisposeResources();
            Application.Current.Shutdown(failure is null ? 0 : 1);
        }
    }
}
