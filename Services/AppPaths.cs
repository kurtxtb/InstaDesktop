using System;
using System.IO;

namespace InstaDesktop.Services;

public static class AppPaths
{
    public static string Root { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstaDesktop");
    public static string UserData => Path.Combine(Root, "UserData");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");

    // Diagnostics always use a separate profile, never the user's logged-in profile.
    internal static void UseDiagnosticRoot(string root) => Root = Path.GetFullPath(root);
}
