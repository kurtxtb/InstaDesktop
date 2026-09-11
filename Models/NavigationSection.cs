using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InstaDesktop.Models;

public enum NavigationSection { Home, Messages, Reels, Explore, Search, Notifications, Profile, Create, Other }

public static class InstagramRoutes
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts", "about", "legal", "privacy", "terms", "challenge", "checkpoint", "stories",
        "p", "reel", "tv", "share", "oauth", "download", "developer", "api", "web", "emails"
    };

    public static string? PathFor(NavigationSection section) => section switch
    {
        NavigationSection.Home => "/",
        NavigationSection.Messages => "/direct/inbox/",
        NavigationSection.Reels => "/reels/",
        NavigationSection.Explore => "/explore/",
        NavigationSection.Notifications => "/accounts/activity/",
        _ => null
    };

    public static NavigationSection FromPath(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return NavigationSection.Home;
        string first = segments[0].ToLowerInvariant();
        if (first == "direct") return NavigationSection.Messages;
        if (first is "reels" or "reel") return NavigationSection.Reels;
        if (first == "explore") return NavigationSection.Explore;
        if (first == "accounts" && segments.Length > 1 &&
            segments[1].Equals("activity", StringComparison.OrdinalIgnoreCase))
            return NavigationSection.Notifications;
        if (segments.Length == 1 && !Reserved.Contains(first) && Regex.IsMatch(first, @"^[a-z0-9_.]{1,30}$"))
            return NavigationSection.Profile;
        return NavigationSection.Other;
    }

    public static string TitleFor(NavigationSection section) => section == NavigationSection.Other ? "Instagram" : section.ToString();
}
