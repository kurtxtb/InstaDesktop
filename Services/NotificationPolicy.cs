using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using InstaDesktop.Models;

namespace InstaDesktop.Services;

public static class NotificationPolicy
{
    public static bool IsInstagramOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host == "www.instagram.com" || uri.Host == "instagram.com");

    public static string? DirectUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 || value.Contains('\\') ||
            value.Contains('%') || value.Contains('?') || value.Contains('#')) return null;
        string absolute = value.StartsWith("/direct/t/", StringComparison.Ordinal)
            ? "https://www.instagram.com" + value : value;
        if (!IsInstagramOrigin(absolute) || !Uri.TryCreate(absolute, UriKind.Absolute, out var uri) ||
            !Regex.IsMatch(uri.AbsolutePath, @"\A/direct/t/[0-9]{1,64}/?\z", RegexOptions.CultureInvariant) ||
            absolute.Contains("/./", StringComparison.Ordinal) || absolute.Contains("/../", StringComparison.Ordinal)) return null;
        return "https://www.instagram.com" + uri.AbsolutePath.TrimEnd('/') + "/";
    }

    // No cookies or WebView credentials are sent when fetching public CDN images.
    // Redirects are disabled by the downloader so this allowlist also covers the final host.
    public static string? ImageUrl(string? value)
    {
        if (value is null || value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !(uri.Host == "instagram.com" || uri.Host.EndsWith(".instagram.com", StringComparison.Ordinal) ||
              uri.Host.EndsWith(".cdninstagram.com", StringComparison.Ordinal) ||
              uri.Host.EndsWith(".fbcdn.net", StringComparison.Ordinal))) return null;
        return uri.AbsoluteUri;
    }

    public static string Clean(string? value, int max)
    {
        // Preserve ZWJ/variation selectors used by emoji, while excluding XML
        // control characters. Never split a UTF-16 surrogate pair at the limit.
        string text = Regex.Replace(value ?? "", @"[\p{Cc}]+", " ").Trim();
        int length = Math.Min(text.Length, max);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    public static InstagramNotification Normalize(InstagramNotification n) => n with
    {
        Title = string.IsNullOrWhiteSpace(n.Title) ? "Instagram" : Clean(n.Title, 160),
        Body = string.IsNullOrWhiteSpace(n.Body) ? "You have a new message" : Clean(n.Body, 1000),
        SenderName = Clean(n.SenderName, 160), ThreadUrl = DirectUrl(n.ThreadUrl),
        AvatarUrl = ImageUrl(n.AvatarUrl), ImageUrl = ImageUrl(n.ImageUrl),
        Origin = IsInstagramOrigin(n.Origin) ? "https://www.instagram.com" : "",
        Id = Clean(n.Id, 256), Tag = Clean(n.Tag, 256)
    };

    public static bool TryParse(string origin, string json, out InstagramNotification? notification)
    {
        notification = null;
        if (!IsInstagramOrigin(origin) || json.Length > 16384) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type", 64) != "instadesktop:notification") return false;
            var source = Text(root, "source", 32) switch
            {
                "page" => NotificationSource.PageNotification,
                "dom" => NotificationSource.DirectDom,
                "badge" => NotificationSource.UnreadBadge,
                _ => (NotificationSource?)null
            };
            if (source is null) return false;
            string? sequence = Text(root, "sequence", 100);
            string? thread = Text(root, "threadUrl", 256);
            if (thread is not null && DirectUrl(thread) is null) return false;
            if (source >= NotificationSource.DirectDom &&
                (!root.TryGetProperty("unread", out var unread) || unread.ValueKind != JsonValueKind.True || string.IsNullOrEmpty(sequence))) return false;
            if (source == NotificationSource.DirectDom && thread is null) return false;
            notification = Normalize(new InstagramNotification
            {
                Source = source.Value, Origin = new Uri(origin).GetLeftPart(UriPartial.Authority),
                Type = source >= NotificationSource.DirectDom || thread is not null ? InstagramNotificationType.DirectMessage : InstagramNotificationType.Instagram,
                Id = Text(root, "id", 256), Tag = Text(root, "tag", 256), StateSequence = sequence,
                Title = source == NotificationSource.UnreadBadge ? "Instagram" : Text(root, "title", 160) ?? "Instagram",
                Body = source == NotificationSource.UnreadBadge ? "You have a new message" : Text(root, "body", 1000) ?? "You have a new message",
                SenderName = Text(root, "senderName", 160), ThreadUrl = thread,
                AvatarUrl = Text(root, "avatarUrl", 4096), ImageUrl = Text(root, "imageUrl", 4096),
                Silent = root.TryGetProperty("silent", out var silent) && silent.ValueKind == JsonValueKind.True
            });
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    private static string? Text(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > max)
            throw new JsonException();
        return text;
    }
}
