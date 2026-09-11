using System;

namespace InstaDesktop.Services;

public static class NavigationPolicy
{
    public const string Home = "https://www.instagram.com/";
    public const string RuntimeDownload = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static bool IsInstagramHost(string host) =>
        host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsTrusted(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && IsInstagramHost(uri.IdnHost);

    public static bool TryExternal(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)) return false;
        uri = candidate;
        return true;
    }

    public static string? UpgradeInstagramHttp(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !IsInstagramHost(uri.IdnHost)) return null;
        return new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
    }
}
