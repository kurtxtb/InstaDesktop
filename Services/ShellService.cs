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

    // Fixed Windows Settings pages only; nothing from web content is launched.
    internal static Action<string>? DiagnosticSettingsOpened { get; set; }
    public static bool OpenPrivacySettings(bool camera)
    {
        string page = camera ? "ms-settings:privacy-webcam" : "ms-settings:privacy-microphone";
        LoggingService.Write(LogEvent.MediaPrivacySettingsOpened, code: camera ? 2 : 1);
        try
        {
            if (DiagnosticSettingsOpened is { } diagnostic) { diagnostic(page); return true; }
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
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

    // Shows a downloaded file selected in Explorer.
    public static void ShowInFolder(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{file}\"", UseShellExecute = true }); }
        catch (Exception e) { LoggingService.Write(LogEvent.UnexpectedException, e); }
    }

    // The user's Downloads folder (it can be moved, so not just %USERPROFILE%\Downloads).
    public static string DownloadsFolder
    {
        get
        {
            var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out IntPtr path) == 0)
            {
                try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(path)!; }
                finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path); }
            }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id,
        uint flags, IntPtr token, out IntPtr path);
}
