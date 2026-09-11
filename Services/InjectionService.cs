using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop.Services;

public sealed class InjectionService
{
    private string? _registration;
    private string? _lastScript;

    public async Task ConfigureAsync(CoreWebView2 core, bool enabled, bool compact = true)
    {
        string? script = enabled ? await BuildScriptAsync(compact) : null;
        if (script == _lastScript) return;
        // Register the replacement first so a bad edit does not remove the previous setup.
        string? next = script is null ? null : await core.AddScriptToExecuteOnDocumentCreatedAsync(script)
            .WaitAsync(TimeSpan.FromSeconds(10));
        if (_registration is not null) core.RemoveScriptToExecuteOnDocumentCreated(_registration);
        _registration = next;
        _lastScript = script;
    }

    public static async Task<string> BuildScriptAsync(bool compact = true)
    {
        string css = await ReadAssetAsync("Styles/custom.css");
        string emoji = await ReadAssetAsync("Scripts/modules/emoji.js");
        string js = await ReadAssetAsync("Scripts/inject.js");
        // Code executes only in the top-level trusted document. No eval/CSP bypass is used.
        return "(() => {\n" +
            "const h = location.hostname.toLowerCase();\n" +
            "if (window !== window.top || location.protocol !== 'https:' || " +
            "(location.port && location.port !== '443') || !(h === 'instagram.com' || h.endsWith('.instagram.com'))) return;\n" +
            "let reported = false; const report = () => { if (!reported) { reported = true; " +
            "window.chrome?.webview?.postMessage('instadesktop:injection-error'); } };\n" +
            "try { const customCss = " + JsonSerializer.Serialize(css) + "; const reportError = report; const desktopOptions = { compact: " + (compact ? "true" : "false") + " };\n" +
            emoji + "\n" + js + "\n} catch { report(); }\n" +
            "})();";
    }

    public async Task RefreshAsync(CoreWebView2 core, bool enabled, bool compact)
    {
        await ConfigureAsync(core, enabled, compact);
        if (!NavigationPolicy.IsTrusted(core.Source)) return;
        if (enabled && _lastScript is not null)
            // ExecuteScript can wait behind Instagram's main-thread work. Keep the
            // existing document alive and give WebView2 a bounded, practical window.
            await core.ExecuteScriptAsync(_lastScript).WaitAsync(TimeSpan.FromSeconds(30));
        else await core.ExecuteScriptAsync("window.__InstaDesktopCustomization?.dispose(); delete window.__InstaDesktopCustomization;");
    }

    private static async Task<string> ReadAssetAsync(string relativePath)
    {
        string path = Path.Combine(AppPaths.Assets, relativePath);
        if (File.Exists(path)) return await File.ReadAllTextAsync(path);
        // A copied single EXE works by itself. Materialize editable defaults when
        // possible; read-only locations can still use the embedded resources.
        string resource = "InstaDesktop.Assets." + relativePath.Replace('/', '.');
        await using var stream = typeof(InjectionService).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("Default customization asset is missing.");
        using var reader = new StreamReader(stream);
        string content = await reader.ReadToEndAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await using var writer = new StreamWriter(output);
            await writer.WriteAsync(content);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return content;
    }
}
