using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace InstaDesktop.Services;

// Single owner of the microphone/camera rules. Deliberately narrower than
// NavigationPolicy: only the two exact Instagram origins may even ask.
//
// Profile contract (verified against the WebView2 Runtime by the
// --media-permission-test probe):
// - A decision saved in the profile suppresses later PermissionRequested events,
//   so an app-side "switch is off" denial is never saved.
// - The app only grants with SavesInProfile = true. Clearing that saved grant
//   (Allow -> Default) ends live capture tracks; a request-only grant would not.
// - While a switch is off both origins hold Default, so every request reaches
//   the handler, is denied for that request only and can be explained.
// - While a switch is on, the user's own site answer (Allow/Deny) is kept across
//   restarts. Turning the switch on again or resetting website permissions
//   clears a saved Deny so Instagram asks again; a grant is never created here.
internal static class MediaPermissionPolicy
{
    internal static readonly string[] Origins = { "https://www.instagram.com", "https://instagram.com" };
    internal static readonly CoreWebView2PermissionKind[] Kinds = { CoreWebView2PermissionKind.Microphone, CoreWebView2PermissionKind.Camera };

    // Log codes; never reorder.
    internal enum Reason { Prompt = 1, SettingOff = 2, UntrustedOrigin = 3, BackgroundPage = 4, UserAllowed = 5, UserDenied = 6, Stale = 7, Canceled = 8, Failed = 9 }
    internal readonly record struct Decision(bool Prompt, Reason Reason);

    internal static bool IsMediaKind(CoreWebView2PermissionKind kind) =>
        kind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera;

    internal static bool IsMediaOrigin(string? uri) => NotificationPolicy.IsInstagramOrigin(uri);

    internal static bool Enabled(Models.AppSettings settings, CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => settings.AllowMicrophone,
        CoreWebView2PermissionKind.Camera => settings.AllowCamera,
        _ => false
    };

    // Primary page. A denial here is always request-only (see contract above).
    internal static Decision ForPrimary(CoreWebView2PermissionKind kind, string? uri, bool enabled) =>
        !IsMediaKind(kind) || !IsMediaOrigin(uri) ? new(false, Reason.UntrustedOrigin)
        : !enabled ? new(false, Reason.SettingOff)
        : new(true, Reason.Prompt);

    // Hidden inbox monitor: never prompts, never grants, never saves.
    internal static Decision ForMonitor(CoreWebView2PermissionKind kind, string? uri) => new(false, Reason.BackgroundPage);

    // Fixed origin classes for logs; no host or URL is written.
    internal static int OriginCategory(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var value)) return 9;
        if (value.Scheme != Uri.UriSchemeHttps) return 8;
        if (!value.IsDefaultPort || value.UserInfo.Length > 0) return 7;
        string host = value.IdnHost.ToLowerInvariant();
        if (host == "www.instagram.com") return 1;
        if (host == "instagram.com") return 2;
        if (host.EndsWith(".instagram.com", StringComparison.Ordinal)) return 3;
        foreach (string meta in new[] { "facebook.com", "messenger.com", "fbcdn.net", "cdninstagram.com", "meta.com", "fbsbx.com" })
            if (host == meta || host.EndsWith("." + meta, StringComparison.Ordinal)) return 4;
        return 5;
    }

    // source: 1 primary, 2 monitor. Digits: saves | source | kind | origin | reason(2) | state
    internal static int LogCode(int source, CoreWebView2PermissionKind kind, string? uri, Reason reason, CoreWebView2PermissionState state, bool saves) =>
        (saves ? 1000000 : 0) + source * 100000 + (int)kind * 10000 + OriginCategory(uri) * 1000 + (int)reason * 10 + (int)state;

    // Brings both origins to the state the switch requires. Re-reads the switch
    // after each pass so a toggle during the async calls cannot leave the
    // origins disagreeing with the final setting.
    internal static async Task SyncAsync(CoreWebView2Profile profile, CoreWebView2PermissionKind kind, Func<bool> enabled, Func<bool> clearDenied)
    {
        bool applied;
        do
        {
            applied = enabled();
            bool clear = !applied || clearDenied();
            var saved = await profile.GetNonDefaultPermissionSettingsAsync();
            foreach (string origin in Origins)
            {
                var state = StateOf(saved, kind, origin);
                // Off: remove Allow (stops live capture) and Deny (so requests stay explainable).
                // On: remove only a Deny, and only when explicitly asked to.
                if (state == CoreWebView2PermissionState.Default || (applied && state == CoreWebView2PermissionState.Allow) || !clear) continue;
                await profile.SetPermissionStateAsync(kind, origin, CoreWebView2PermissionState.Default);
            }
        } while (applied != enabled());
        await LogStateAsync(profile, kind);
    }

    internal static CoreWebView2PermissionState StateOf(System.Collections.Generic.IEnumerable<CoreWebView2PermissionSetting> saved,
        CoreWebView2PermissionKind kind, string origin) =>
        saved.FirstOrDefault(p => p.PermissionKind == kind &&
            string.Equals(p.PermissionOrigin.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))?.PermissionState
        ?? CoreWebView2PermissionState.Default;

    // Code: kind * 100 + www + 10 * root (Default=0, Allow=1, Deny=2).
    internal static async Task<int> LogStateAsync(CoreWebView2Profile profile, CoreWebView2PermissionKind kind)
    {
        var saved = await profile.GetNonDefaultPermissionSettingsAsync();
        int code = (int)kind * 100 + (int)StateOf(saved, kind, Origins[0]) + 10 * (int)StateOf(saved, kind, Origins[1]);
        LoggingService.Write(LogEvent.MediaPermissionState, code: code);
        return code;
    }

    // Combined site state shown in Settings: any Deny wins, then any Allow.
    internal static async Task<CoreWebView2PermissionState> SiteStateAsync(CoreWebView2Profile profile, CoreWebView2PermissionKind kind)
    {
        var saved = await profile.GetNonDefaultPermissionSettingsAsync();
        var states = Origins.Select(o => StateOf(saved, kind, o)).ToArray();
        return states.Contains(CoreWebView2PermissionState.Deny) ? CoreWebView2PermissionState.Deny
            : states.Contains(CoreWebView2PermissionState.Allow) ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Default;
    }

    // Runs in every document of the hidden inbox monitor, before page script.
    // A grant saved for the primary page applies to the whole shared profile and
    // bypasses PermissionRequested (probe F7), so the capture entry points are
    // removed there. The primary page is never patched.
    internal const string MonitorCaptureBlockScript = """
        (()=>{try{const hide=(o,n)=>{if(o&&n in o)Object.defineProperty(o,n,{configurable:false,enumerable:false,get(){return undefined;}});};
        hide(Navigator.prototype,'mediaDevices');hide(Navigator.prototype,'getUserMedia');hide(Navigator.prototype,'webkitGetUserMedia');}catch(e){}})();
        """;
}
