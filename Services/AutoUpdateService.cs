using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InstaDesktop.Services;

// Optional startup updater. Configure INSTA_UPDATE_MANIFEST_URL (or place
// UpdateManifestUrl.txt beside the executable) with a JSON manifest URL.
public sealed class AutoUpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<bool> TryUpdateAsync(CancellationToken cancellationToken = default)
    {
        string? manifestUrl = Environment.GetEnvironmentVariable("INSTA_UPDATE_MANIFEST_URL")
            ?? "https://api.github.com/repos/kurtxtb/InstaDesktop/releases/latest";
        string localUrlFile = Path.Combine(AppContext.BaseDirectory, "UpdateManifestUrl.txt");
        if (string.IsNullOrWhiteSpace(manifestUrl) && File.Exists(localUrlFile))
            manifestUrl = (await File.ReadAllTextAsync(localUrlFile, cancellationToken)).Trim();
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        Client.DefaultRequestHeaders.UserAgent.ParseAdd("InstaDesktop-Updater/1.0");
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(
            await Client.GetStreamAsync(uri, cancellationToken), cancellationToken: cancellationToken);
        string? installerUrl = manifest?.InstallerUrl;
        if (manifest?.Assets is { Length: > 0 })
            installerUrl = Array.Find(manifest.Assets, a => a.Name.Equals("InstaDesktop-Setup.exe", StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl;
        string versionText = manifest?.Version?.TrimStart('v') ?? "";
        if (manifest is null || !Version.TryParse(versionText, out var remote) ||
            remote <= Assembly.GetExecutingAssembly().GetName().Version ||
            !Uri.TryCreate(installerUrl, UriKind.Absolute, out var installerUri) ||
            installerUri.Scheme != Uri.UriSchemeHttps) return false;

        string installer = Path.Combine(Path.GetTempPath(), $"InstaDesktop-Setup-{remote}.exe");
        await using (var source = await Client.GetStreamAsync(installerUri, cancellationToken))
        await using (var target = File.Create(installer))
            await source.CopyToAsync(target, cancellationToken);
        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            await using var file = File.OpenRead(installer);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!hash.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)) { File.Delete(installer); return false; }
        }
        Process.Start(new ProcessStartInfo(installer, "/SILENT /CLOSEAPPLICATIONS /NORESTART") { UseShellExecute = true });
        return true;
    }

    private sealed record Manifest(string? Version, string? InstallerUrl, string? Sha256, Asset[]? Assets);
    private sealed record Asset(string Name, string BrowserDownloadUrl);
}
