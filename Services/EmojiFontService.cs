using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop.Services;

// Serves the bundled Twemoji font on Instagram's own origin, which its CSP
// font-src already allows. The font never leaves the machine.
public static class EmojiFontService
{
    private const string FontPath = "/__instadesktop/emoji/Twemoji.woff2";
    private static readonly Lazy<byte[]?> Font = new(LoadFont);

    public static async Task ConfigureAsync(CoreWebView2 core)
    {
        if (Font.Value is null) return;
        foreach (string origin in new[] { "https://www.instagram.com", "https://instagram.com" })
            // All source kinds: Instagram's service worker may fetch on the page's behalf.
            core.AddWebResourceRequestedFilter(origin + FontPath, CoreWebView2WebResourceContext.Font,
                CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += WebResourceRequested;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(await WebViewService.ReadNotificationAssetAsync("emoji-font.js"));
    }

    private static void WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core || Font.Value is not { } font ||
            !NotificationPolicy.IsInstagramOrigin(e.Request.Uri) ||
            new Uri(e.Request.Uri).AbsolutePath != FontPath) return;
        e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(font, writable: false), 200, "OK",
            "Content-Type: font/woff2\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=31536000, immutable");
    }

    private static byte[]? LoadFont()
    {
        try
        {
            string path = Path.Combine(AppPaths.Assets, "Fonts", "Twemoji.woff2");
            if (File.Exists(path)) return File.ReadAllBytes(path);
            using var stream = typeof(EmojiFontService).Assembly.GetManifestResourceStream("InstaDesktop.Assets.Fonts.Twemoji.woff2");
            if (stream is null) return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception error)
        {
            LoggingService.Write(LogEvent.InjectionError, error);
            return null;
        }
    }
}
