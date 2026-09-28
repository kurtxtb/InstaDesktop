using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop.Services;

// Single owner of the Notifications permission rules for both controllers. The
// primary and hidden inbox WebViews share one profile, so they must agree.
internal static class NotificationPermissionPolicy
{
    // Exact origins (no path): the profile stores one content setting per origin.
    internal static readonly string[] InstagramOrigins = { "https://www.instagram.com", "https://instagram.com" };

    internal readonly record struct Decision(CoreWebView2PermissionState State, bool SavesInProfile);

    // The hidden monitor never prompts and never widens anything except Instagram
    // notifications. A grant is saved like the settings sync does; a denial is
    // request-only so the background page can never overwrite the profile
    // state, which the settings flow (SyncAsync) alone owns.
    internal static Decision ForMonitor(CoreWebView2PermissionKind kind, string? uri, bool enabled) =>
        kind == CoreWebView2PermissionKind.Notifications && enabled && NotificationPolicy.IsInstagramOrigin(uri)
            ? new(CoreWebView2PermissionState.Allow, true)
            : new(CoreWebView2PermissionState.Deny, false);

    // Primary page: notifications follow the setting without a dialog.
    internal static Decision ForPrimaryNotifications(string? uri, bool enabled) =>
        new(enabled && NotificationPolicy.IsInstagramOrigin(uri) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny, true);

    internal static async Task SyncAsync(CoreWebView2Profile profile, Func<bool> enabled)
    {
        // Re-read after each pass so a toggle during the async calls cannot leave
        // the two origins disagreeing with the final setting.
        bool applied;
        do
        {
            applied = enabled();
            var state = applied ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            foreach (string origin in InstagramOrigins)
                await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, origin, state);
        } while (applied != enabled());
        LoggingService.Write(LogEvent.NotificationPermission, code: applied ? 1 : 0);
        await LogStateAsync(profile);
    }

    // Read back what the profile actually stores: www + 10 * root, using
    // CoreWebView2PermissionState values (Default=0, Allow=1, Deny=2).
    internal static async Task<int> LogStateAsync(CoreWebView2Profile profile)
    {
        var saved = await profile.GetNonDefaultPermissionSettingsAsync();
        int State(string origin) => (int)(saved.FirstOrDefault(p => p.PermissionKind == CoreWebView2PermissionKind.Notifications &&
            string.Equals(p.PermissionOrigin.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))?.PermissionState
            ?? CoreWebView2PermissionState.Default);
        int code = State(InstagramOrigins[0]) + 10 * State(InstagramOrigins[1]);
        LoggingService.Write(LogEvent.NotificationPermissionState, code: code);
        return code;
    }
}
