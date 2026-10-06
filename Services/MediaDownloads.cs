using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InstaDesktop.Services;

// One post (or story item) as Instagram describes it: the best video file,
// the largest picture, the picture file names (to recognize what is on
// screen) and album items.
internal sealed record MediaItem(int Type, string? Video, string? Image, IReadOnlyList<string> Files);
internal sealed record MediaInfo(string? Code, string? Pk, string? Owner, MediaItem Item, IReadOnlyList<MediaItem> Children);

// What was right-clicked, as reported by media-download.js.
internal sealed record MediaTarget(string Kind, string Src, string File, IReadOnlyList<string> Near, string? Code, string? Story);

internal enum MediaDownloadOutcome { Saved, NothingHere, VideoUnavailable, Failed }
internal sealed record MediaDownloadResult(MediaDownloadOutcome Outcome, string? Path = null);

// Right-click > Download photo / video. Post data comes from the page itself
// and from Instagram's API responses as you scroll (kept in memory only, never
// logged). Only Instagram's own media servers are downloaded from.
internal sealed class MediaDownloads
{
    private const int Capacity = 1500;
    private readonly Dictionary<string, MediaInfo> _index = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        // No cookies: these are signed public CDN addresses.
        var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) InstaDesktop");
        return client;
    }

    internal int Count => _index.Count;
    internal MediaInfo? Find(string? key) => key is not null && _index.TryGetValue(key, out var info) ? info : null;

    // ---- Indexing ---------------------------------------------------------

    // An API response body (some are several JSON documents, or start with for(;;);).
    internal void AddResponse(string body)
    {
        if (body.Length > 16 * 1024 * 1024 || !body.Contains("image_versions2", StringComparison.Ordinal)) return;
        body = body.Trim();
        if (body.StartsWith("for (;;);", StringComparison.Ordinal)) body = body[9..];
        // One document, or (streamed responses) one document per line.
        if (TryWalk(body)) return;
        foreach (var part in body.Split('\n')) TryWalk(part.Trim());
    }

    private bool TryWalk(string text)
    {
        if (text.Length < 2 || text[0] != '{') return false;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
            Walk(document.RootElement, 0);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private void Walk(JsonElement element, int depth)
    {
        if (depth > 120) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) Walk(child, depth + 1);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        if (Parse(element) is { } info) Add(info);
        foreach (var property in element.EnumerateObject()) Walk(property.Value, depth + 1);
    }

    internal void Add(MediaInfo info)
    {
        foreach (var key in new[] { info.Code, info.Pk })
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (!_index.ContainsKey(key)) _order.Enqueue(key);
            _index[key] = info;
        }
        while (_order.Count > Capacity && _order.TryDequeue(out var old)) _index.Remove(old);
    }

    private static MediaInfo? Parse(JsonElement o)
    {
        bool media = o.TryGetProperty("image_versions2", out _) || o.TryGetProperty("video_versions", out _) || o.TryGetProperty("carousel_media", out _);
        if (!media) return null;
        string? code = Text(o, "code");
        string? pk = o.TryGetProperty("pk", out var p) ? (p.ValueKind == JsonValueKind.Number ? p.GetRawText() : p.ValueKind == JsonValueKind.String ? p.GetString() : null) : null;
        if (code is null && pk is null) return null;
        string? owner = o.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? Text(user, "username")
            : o.TryGetProperty("owner", out var owner2) && owner2.ValueKind == JsonValueKind.Object ? Text(owner2, "username") : null;
        var children = new List<MediaItem>();
        if (o.TryGetProperty("carousel_media", out var carousel) && carousel.ValueKind == JsonValueKind.Array)
            foreach (var child in carousel.EnumerateArray()) if (child.ValueKind == JsonValueKind.Object) children.Add(Item(child));
        return new MediaInfo(code, pk, owner, Item(o), children);
    }

    private static MediaItem Item(JsonElement o)
    {
        int type = o.TryGetProperty("media_type", out var t) && t.TryGetInt32(out int value) ? value : 0;
        string? video = null;
        if (o.TryGetProperty("video_versions", out var videos) && videos.ValueKind == JsonValueKind.Array)
            video = videos.EnumerateArray().Select(v => Text(v, "url")).FirstOrDefault(u => u is not null);
        string? image = null;
        var files = new List<string>();
        if (o.TryGetProperty("image_versions2", out var iv) && iv.ValueKind == JsonValueKind.Object &&
            iv.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            int width = -1;
            foreach (var c in candidates.EnumerateArray())
            {
                if (Text(c, "url") is not { } url) continue;
                files.Add(FileOf(url));
                int w = c.TryGetProperty("width", out var cw) && cw.TryGetInt32(out int x) ? x : 0;
                if (w > width) { width = w; image = url; }
            }
        }
        return new MediaItem(type, video, image, files);
    }

    private static string? Text(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static string FileOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.Split('/').LastOrDefault() ?? "" : "";

    // From media-download.js (lookup or take); null for anything malformed.
    internal static MediaInfo? FromPage(string json) => FromPage(json, out _);

    internal static MediaInfo? FromPage(string json, out MediaTarget? target)
    {
        target = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var o = document.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            if (Text(o, "kind") is { } kind)
            {
                target = new MediaTarget(kind, Text(o, "src") ?? "", Text(o, "file") ?? "",
                    o.TryGetProperty("near", out var near) && near.ValueKind == JsonValueKind.Array
                        ? near.EnumerateArray().Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToList() : new List<string>(),
                    Text(o, "code"), Text(o, "story"));
                return null;
            }
            MediaItem Page(JsonElement m) => new(
                m.TryGetProperty("type", out var t) && t.TryGetInt32(out int type) ? type : 0, Text(m, "video"), Text(m, "image"),
                m.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                    ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>());
            var children = o.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(Page).ToList() : new List<MediaItem>();
            return new MediaInfo(Text(o, "code"), Text(o, "pk"), Text(o, "owner"), Page(o), children);
        }
        catch (JsonException) { return null; }
    }

    // ---- Choosing the file ------------------------------------------------

    // The address to save for what was clicked, given what is known about its post.
    internal static string? Choose(MediaTarget target, MediaInfo? info)
    {
        var items = info is null ? new List<MediaItem>() : info.Children.Count > 0 ? info.Children.ToList() : new List<MediaItem> { info.Item };
        if (target.Kind == "video")
        {
            var videos = items.Where(i => i.Video is not null).ToList();
            var byPreview = videos.FirstOrDefault(i => i.Files.Intersect(target.Near).Any());
            var chosen = byPreview ?? (videos.Count == 1 ? videos[0] : null);
            return chosen?.Video ?? (IsMediaHost(target.Src) ? target.Src : null);
        }
        // A photo: the largest version of the very picture on screen.
        var match = items.FirstOrDefault(i => i.Image is not null && i.Files.Contains(target.File));
        return match?.Image ?? (IsMediaHost(target.Src) ? target.Src : null);
    }

    // Instagram's media servers only (cdninstagram.com, fbcdn.net), over HTTPS.
    internal static bool IsMediaHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.EndsWith(".cdninstagram.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase));

    // ---- Saving -----------------------------------------------------------

    internal static string BaseName(MediaTarget target, MediaInfo? info)
    {
        string? owner = info?.Owner;
        string? id = target.Code ?? info?.Code ?? target.Story ?? info?.Pk;
        string name = owner is not null && id is not null ? owner + "_" + id
            : id is not null ? "instagram_" + id
            : "instagram_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        if (info is not null && info.Children.Count > 1)
        {
            int index = info.Children.ToList().FindIndex(c => c.Files.Contains(target.File) || c.Files.Intersect(target.Near).Any());
            if (index >= 0) name += "_" + (index + 1);
        }
        return Regex.Replace(name, @"[^A-Za-z0-9._-]", "_");
    }

    // Downloads into folder (or exactly to file when given); returns the saved path.
    internal static async Task<string> SaveAsync(string url, string folder, string baseName, string? file = null, CancellationToken cancel = default)
    {
        if (!IsMediaHost(url)) throw new InvalidOperationException("Not an Instagram media address.");
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        string extension = response.Content.Headers.ContentType?.MediaType switch
        {
            "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/heic" => ".heic",
            "image/gif" => ".gif", "video/mp4" => ".mp4", "video/quicktime" => ".mov",
            _ => Path.GetExtension(new Uri(url).AbsolutePath) is { Length: > 1 and < 6 } ext ? ext : ".bin"
        };
        Directory.CreateDirectory(folder);
        string target = file ?? Unique(Path.Combine(folder, baseName + extension));
        string temporary = target + ".download";
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var output = File.Create(temporary))
            await source.CopyToAsync(output, cancel);
        File.Move(temporary, target, overwrite: file is not null);
        return target;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string folder = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
