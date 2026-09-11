using System;
using Microsoft.Win32;

namespace InstaDesktop.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "InstaDesktop";
    public static string Command => $"\"{Environment.ProcessPath ?? throw new InvalidOperationException()}\" --background";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return !string.IsNullOrWhiteSpace(key?.GetValue(Name) as string);
    }

    public static string? ReadRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) as string;
    }

    public static void SetEnabled(bool enabled) => RestoreRegistration(enabled ? Command : null);

    public static void RestoreRegistration(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (command is null) key.DeleteValue(Name, throwOnMissingValue: false);
        else key.SetValue(Name, command, RegistryValueKind.String);
    }
}
