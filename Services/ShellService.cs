using System;
using System.Diagnostics;

namespace InstaDesktop.Services;

public static class ShellService
{
    internal static Action<Uri>? DiagnosticExternalOpened { get; set; }
    public static bool OpenWeb(string value)
    {
        if (!NavigationPolicy.TryExternal(value, out var uri)) return false;
        try
        {
            if (DiagnosticExternalOpened is { } diagnostic) { diagnostic(uri!); return true; }
            Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            LoggingService.Write(LogEvent.UnexpectedException, e);
            return false;
        }
    }

    public static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{path}\"", UseShellExecute = true });
    }
}
