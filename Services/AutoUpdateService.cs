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

// Startup updater. Reads the latest GitHub Release (or INSTA_UPDATE_MANIFEST_URL,
// or UpdateManifestUrl.txt beside the executable, with the same JSON shape or
// the simple {version, installerUrl, sha256} manifest), downloads the installer
// only when that release is newer, verifies its SHA-256 and runs it silently.
public sealed class AutoUpdateService
{
    internal const string LatestReleaseUrl = "https://api.github.com/repos/kurtxtb/InstaDesktop/releases/latest";
    internal const string InstallerName = "InstaDesktop-Setup.exe";
    internal static string ReleasePage(Version version) =>
        "https://github.com/kurtxtb/InstaDesktop/releases/tag/v" + version.ToString(3);
    private static readonly HttpClient Client = CreateClient();

    internal sealed record UpdateInfo(Version Version, Uri InstallerUri, string Sha256);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("InstaDesktop-Updater/1.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // Check and install in one step (startup). background: relaunch into the tray.
    public async Task<bool> TryUpdateAsync(bool background, CancellationToken cancellationToken = default)
    {
        if (await CheckAsync(cancellationToken) is not { } update ||
            await DownloadAsync(update, cancellationToken) is not { } installer) return false;
        Launch(installer, background);
        return true;
    }

    // Returns a newer, verified-looking release, or null when there is none.
    internal async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        string? manifestUrl = Environment.GetEnvironmentVariable("INSTA_UPDATE_MANIFEST_URL");
        string localUrlFile = Path.Combine(AppContext.BaseDirectory, "UpdateManifestUrl.txt");
        if (string.IsNullOrWhiteSpace(manifestUrl) && File.Exists(localUrlFile))
            manifestUrl = (await File.ReadAllTextAsync(localUrlFile, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(manifestUrl)) manifestUrl = LatestReleaseUrl;
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        LoggingService.Write(LogEvent.UpdateCheck);
        string json;
        using (var metadata = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, metadata.Token))
            json = await Client.GetStringAsync(uri, linked.Token);
        var current = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
        if (Parse(json, requireGitHubHost: manifestUrl == LatestReleaseUrl) is not { } update)
        {
            LoggingService.Write(LogEvent.UpdateUnavailable, code: 1);
            return null;
        }
        if (!IsNewer(update.Version, current))
        {
            LoggingService.Write(LogEvent.UpdateUnavailable, code: 0);
            return null;
        }
        LoggingService.Write(LogEvent.UpdateAvailable, code: update.Version.Major * 10000 + update.Version.Minor * 100 + update.Version.Build);
        return update;
    }

    // Downloads the installer and returns its path once the SHA-256 matches.
    internal async Task<string?> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        string installer = Path.Combine(Path.GetTempPath(), $"{DownloadPrefix}{update.Version}.exe");
        await using (var source = await Client.GetStreamAsync(update.InstallerUri, cancellationToken))
        await using (var target = File.Create(installer))
            await source.CopyToAsync(target, cancellationToken);
        string hash;
        await using (var file = File.OpenRead(installer))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(installer);
            LoggingService.Write(LogEvent.UpdateVerificationFailed);
            return null;
        }
        return installer;
    }

    internal static void Launch(string installer, bool background)
    {
        // The installer waits for this process to exit, upgrades in place and
        // starts the new version again (RELAUNCH=1), in the tray if BACKGROUND=1.
        string arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1" + (background ? " /BACKGROUND=1" : "");
        Process.Start(new ProcessStartInfo(installer, arguments) { UseShellExecute = true });
        LoggingService.Write(LogEvent.UpdateLaunched, code: background ? 1 : 0);
    }

    private const string DownloadPrefix = "InstaDesktop-Setup-";

    // Installers from earlier updates; the one still running (if any) is skipped.
    public static void DeleteOldDownloads()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetTempPath(), DownloadPrefix + "*.exe"))
            {
                try { File.Delete(file); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LoggingService.Write(LogEvent.UpdateCleanupFailed, error);
        }
    }

    // Accepts the GitHub "latest release" response (tag_name, assets[].name,
    // browser_download_url, digest) or the simple manifest. A hash is required.
    internal static UpdateInfo? Parse(string json, bool requireGitHubHost)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            string? versionText = Text(root, "tag_name") ?? Text(root, "version");
            string? url = Text(root, "installerUrl"), sha = Text(root, "sha256");
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                url = null; sha = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!string.Equals(Text(asset, "name"), InstallerName, StringComparison.OrdinalIgnoreCase)) continue;
                    url = Text(asset, "browser_download_url");
                    string? digest = Text(asset, "digest");
                    if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true) sha = digest[7..];
                    break;
                }
            }
            if (versionText is null || !Version.TryParse(versionText.Trim().TrimStart('v', 'V'), out var version) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var installer) || installer.Scheme != Uri.UriSchemeHttps ||
                sha is null || sha.Length != 64 || !Uri.IsHexDigit(sha[0]) || Convert.FromHexString(sha).Length != 32) return null;
            if (requireGitHubHost && !(installer.Host == "github.com" &&
                installer.AbsolutePath.StartsWith("/kurtxtb/InstaDesktop/releases/download/", StringComparison.Ordinal))) return null;
            return new UpdateInfo(Normalize(version), installer, sha);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { return null; }
    }

    // Compare major.minor.build only: the file version carries a ".0" revision
    // that a v1.2.3 tag never has.
    internal static bool IsNewer(Version remote, Version current) => Normalize(remote) > Normalize(current);
    internal static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
