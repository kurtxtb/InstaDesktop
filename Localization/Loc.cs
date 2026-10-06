using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Markup;

namespace InstaDesktop.Localization;

public enum UiLanguage { System, English, TraditionalChinese }

// The app's own interface text in English and Traditional Chinese. Instagram's
// page keeps its own language. Chosen once at startup (Settings > Language
// applies after a restart); diagnostics always run in English.
public static class Loc
{
    public static bool Chinese { get; private set; }
    // Keys asked for but missing from the table (diagnostics fail on any).
    internal static readonly HashSet<string> MissingKeys = new(StringComparer.Ordinal);

    public static void Use(UiLanguage language) => Chinese = language switch
    {
        UiLanguage.English => false,
        UiLanguage.TraditionalChinese => true,
        _ => IsTraditionalChinese(CultureInfo.CurrentUICulture)
    };

    internal static bool IsTraditionalChinese(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (c.Name is "zh-Hant" or "zh-TW" or "zh-HK" or "zh-MO") return true;
        return false;
    }

    public static string T(string key)
    {
        if (Strings.TryGetValue(key, out var text)) return Chinese ? text.Zh : text.En;
        lock (MissingKeys) MissingKeys.Add(key);
        return key;
    }

    public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    internal static IEnumerable<string> Keys => Strings.Keys;

    private static readonly Dictionary<string, (string En, string Zh)> Strings = new(StringComparer.Ordinal)
    {
        // Main window
        ["Main.Loading"] = ("Loading Instagram...", "正在載入 Instagram…"),
        ["Main.Heading"] = ("Your Instagram, at home.", "你的 Instagram，就在桌面上。"),
        ["Main.HeadingError"] = ("Unable to load Instagram", "無法載入 Instagram"),
        ["Main.Retry"] = ("Retry", "重試"),
        ["Main.GetRuntime"] = ("Get WebView2 Runtime", "取得 WebView2 Runtime"),
        ["Main.Settings"] = ("Settings (Ctrl+,)", "設定 (Ctrl+,)"),
        ["Main.MessagesWindow"] = ("Messages window (Ctrl+Shift+M)", "訊息視窗 (Ctrl+Shift+M)"),
        ["Main.ResetZoom"] = ("Reset zoom (Ctrl+0)", "重設縮放 (Ctrl+0)"),
        ["Main.Minimize"] = ("Minimize", "最小化"),
        ["Main.Maximize"] = ("Maximize", "最大化"),
        ["Main.Restore"] = ("Restore", "還原"),
        ["Main.Close"] = ("Close", "關閉"),
        ["Main.TrayHintTitle"] = ("InstaDesktop is running in the tray", "InstaDesktop 正在系統匣中執行"),
        ["Main.TrayHintText"] = ("Right-click the tray icon for Settings or Exit.", "在系統匣圖示上按右鍵可開啟設定或結束。"),
        ["Main.RestartFailed"] = ("InstaDesktop could not restart. Use Tray > Exit, then start it again.",
            "InstaDesktop 無法重新啟動。請從系統匣選擇「結束」，然後再開啟一次。"),
        ["Main.CallBlockedTitle"] = ("Instagram call blocked", "Instagram 通話已被封鎖"),
        ["Main.CallBlockedMicrophone"] = ("Instagram asked to use your microphone, but it is turned off in InstaDesktop. Click to open Settings.",
            "Instagram 要求使用你的麥克風，但它在 InstaDesktop 中已關閉。按一下即可開啟設定。"),
        ["Main.CallBlockedCamera"] = ("Instagram asked to use your camera, but it is turned off in InstaDesktop. Click to open Settings.",
            "Instagram 要求使用你的相機，但它在 InstaDesktop 中已關閉。按一下即可開啟設定。"),
        ["Main.UnreadOne"] = ("1 unread message", "1 則未讀訊息"),
        ["Main.UnreadMany"] = ("{0} unread messages", "{0} 則未讀訊息"),
        ["Main.PausedUntil"] = ("Notifications paused until {0}", "通知已暫停至 {0}"),
        ["Main.PausedTitle"] = ("Notifications paused", "通知已暫停"),
        ["Main.Resumed"] = ("Notifications resumed", "已恢復通知"),
        ["Main.ResumedText"] = ("New notifications show again.", "新的通知會再次顯示。"),

        // Tray menu
        ["Tray.Open"] = ("Open Instagram", "開啟 Instagram"),
        ["Tray.MessagesWindow"] = ("Messages window", "訊息視窗"),
        ["Tray.Refresh"] = ("Refresh", "重新整理"),
        ["Tray.Pause"] = ("Pause notifications", "暫停通知"),
        ["Tray.PauseOff"] = ("Pause notifications (off in Settings)", "暫停通知（通知已在設定中關閉）"),
        ["Tray.Pause1h"] = ("For 1 hour", "1 小時"),
        ["Tray.Pause8h"] = ("For 8 hours", "8 小時"),
        ["Tray.PauseTomorrow"] = ("Until tomorrow morning", "直到明天早上"),
        ["Tray.Resume"] = ("Resume notifications", "恢復通知"),
        ["Tray.Settings"] = ("Settings...", "設定…"),
        ["Tray.Exit"] = ("Exit", "結束"),

        // Windows notifications
        ["Notify.GenericMessage"] = ("You have a new message", "你有一則新訊息"),
        ["Notify.HiddenMessage"] = ("Sent you a message", "傳送了一則訊息給你"),
        ["Notify.HiddenActivity"] = ("New activity on Instagram", "Instagram 有新動態"),
        ["Notify.Mute"] = ("Mute for 8 hours", "靜音 8 小時"),
        ["Notify.EarlierMessages"] = ("+{0} earlier in this conversation", "此對話還有 {0} 則較早的訊息"),
        ["Notify.HiddenMessages"] = ("Sent you {0} messages", "傳送了 {0} 則訊息給你"),

        // Web view and windows
        ["Web.RuntimeRequired"] = ("Microsoft Edge WebView2 Runtime is required.", "需要 Microsoft Edge WebView2 Runtime。"),
        ["Web.InitFailed"] = ("Unable to initialize Instagram. Retry or check the app log.", "無法初始化 Instagram。請重試或查看應用程式記錄。"),
        ["Web.LoadFailed"] = ("Instagram could not load. Check your connection and try again.", "Instagram 無法載入。請檢查網路連線後再試一次。"),
        ["Web.ReloadFailed"] = ("Instagram could not reload. Try again.", "Instagram 無法重新載入。請再試一次。"),
        ["Web.Unresponsive"] = ("Instagram stopped responding.", "Instagram 沒有回應。"),
        ["Web.Crashed"] = ("Instagram renderer crashed.", "Instagram 頁面已當機。"),
        ["Web.Retrying"] = ("InstaDesktop will retry automatically.", "InstaDesktop 會自動重試。"),
        ["Web.CallWindowFailed"] = ("The Instagram call window could not open.\nTry again, or restart InstaDesktop.",
            "無法開啟 Instagram 通話視窗。\n請再試一次，或重新啟動 InstaDesktop。"),
        ["Web.CallTitle"] = ("Instagram call", "Instagram 通話"),
        ["Web.MessagesTitle"] = ("Instagram Messages", "Instagram 訊息"),
        ["Web.MessagesNotReady"] = ("Instagram is still loading. Try again in a moment.", "Instagram 仍在載入中，請稍後再試。"),
        ["Web.PermissionQuestion"] = ("Allow {0} to use your {1}?", "允許 {0} 使用你的{1}嗎？"),
        ["Web.PermissionMicrophone"] = ("microphone", "麥克風"),
        ["Web.PermissionCamera"] = ("camera", "相機"),
        ["Web.PermissionNotifications"] = ("notifications", "通知"),
        ["Web.PermissionAnd"] = (" and ", "和"),
        ["Web.PermissionRemember"] = ("InstaDesktop remembers your answer. To be asked again, turn the setting off and on in Settings, or use Reset website permissions.",
            "InstaDesktop 會記住你的選擇。若要再次詢問，請在設定中將此選項關閉後再開啟，或使用「重設網站權限」。"),
        ["Web.PermissionResetHint"] = ("You can reset website permissions in Settings.", "你可以在設定中重設網站權限。"),
        ["Web.PermissionTitle"] = ("Instagram permission", "Instagram 權限"),
        ["Web.SaveDownload"] = ("Save Instagram download", "儲存 Instagram 下載"),
        ["Web.AllFiles"] = ("All files (*.*)|*.*", "所有檔案 (*.*)|*.*"),
        ["Call.OnTop"] = ("Always on top", "最上層顯示"),
        ["Media.DownloadPhoto"] = ("Download photo", "下載相片"),
        ["Media.DownloadVideo"] = ("Download video", "下載影片"),
        ["Media.Saved"] = ("Saved {0}", "已儲存 {0}"),
        ["Media.SavedText"] = ("Click to show it in its folder.", "按一下即可在資料夾中顯示。"),
        ["Media.VideoUnavailable"] = ("This video's file isn't available here. Open the post itself and try again.",
            "這裡取得不到這部影片的檔案。請開啟該貼文本身後再試一次。"),
        ["Media.Failed"] = ("The download failed. Check your connection and try again.", "下載失敗，請檢查網路連線後再試一次。"),
        ["Messages.Title"] = ("Messages", "訊息"),
        ["Messages.Pin"] = ("Keep on top", "保持在最上層"),
        ["Messages.Unpin"] = ("Stop keeping on top", "取消保持在最上層"),
        ["Messages.OpenInMain"] = ("Open in the main window", "在主視窗中開啟"),
        ["Messages.Close"] = ("Close (Ctrl+Shift+M)", "關閉 (Ctrl+Shift+M)"),

        // Updates
        ["Update.UpdatedTitle"] = ("InstaDesktop updated to {0}", "InstaDesktop 已更新至 {0}"),
        ["Update.UpdatedText"] = ("Click to see what's new.", "按一下查看更新內容。"),
        ["Update.AvailableTitle"] = ("InstaDesktop {0} is available", "InstaDesktop {0} 已推出"),
        ["Update.AvailableText"] = ("Click to update now. InstaDesktop restarts.", "按一下立即更新，InstaDesktop 會重新啟動。"),
        ["Update.VerifyFailedTitle"] = ("The update could not be verified", "無法驗證更新"),
        ["Update.TryLater"] = ("Try again later.", "請稍後再試。"),
        ["Update.DownloadFailedTitle"] = ("The update could not be downloaded", "無法下載更新"),
        ["Update.CheckConnection"] = ("Check your connection and try again.", "請檢查網路連線後再試一次。"),

        // App
        ["App.StartFailed"] = ("InstaDesktop could not start. Check the app log.", "InstaDesktop 無法啟動。請查看應用程式記錄。"),
        ["App.UnexpectedError"] = ("An unexpected error occurred. InstaDesktop will exit. Your web profile is retained.",
            "發生未預期的錯誤，InstaDesktop 即將結束。你的登入資料會保留。"),

        // Taskbar jump list
        ["Jump.MessagesWindow"] = ("Open messages window", "開啟訊息視窗"),
        ["Jump.Home"] = ("Home", "首頁"),
        ["Jump.Messages"] = ("Messages", "訊息"),
        ["Jump.Reels"] = ("Reels", "Reels"),
        ["Jump.Explore"] = ("Explore", "探索"),
        ["Jump.Pause1h"] = ("Pause notifications for 1 hour", "暫停通知 1 小時"),
        ["Jump.Resume"] = ("Resume notifications", "恢復通知"),

        // Settings window
        ["Settings.WindowTitle"] = ("InstaDesktop Settings", "InstaDesktop 設定"),
        ["Settings.Title"] = ("Settings", "設定"),
        ["Settings.Cancel"] = ("Cancel", "取消"),
        ["Settings.Save"] = ("Save", "儲存"),
        ["Settings.StartupUnavailable"] = ("Windows startup registration is unavailable on this device.", "此裝置無法設定開機時啟動。"),
        ["Settings.LowMemoryUnsupported"] = ("This WebView2 Runtime does not support low-memory mode. Normal mode is used.",
            "此 WebView2 Runtime 不支援低記憶體模式，將使用一般模式。"),
        ["Settings.PrivacyFailedCamera"] = ("Windows Settings could not be opened. Open Settings > Privacy & security > Camera.",
            "無法開啟 Windows 設定。請開啟 設定 > 隱私權與安全性 > 相機。"),
        ["Settings.PrivacyFailedMicrophone"] = ("Windows Settings could not be opened. Open Settings > Privacy & security > Microphone.",
            "無法開啟 Windows 設定。請開啟 設定 > 隱私權與安全性 > 麥克風。"),
        ["Settings.RestartQuestion"] = ("Some changes apply after InstaDesktop restarts. Restart now?", "部分變更要在 InstaDesktop 重新啟動後才會生效。要現在重新啟動嗎？"),
        ["Settings.SavedRelaunch"] = ("Settings were saved. Exit and relaunch to apply them.", "設定已儲存。請結束並重新開啟以套用。"),
        ["Settings.SaveFailed"] = ("Settings could not be saved. Check folder permissions and try again.", "無法儲存設定。請檢查資料夾權限後再試一次。"),
        ["Settings.FolderOpenFailed"] = ("The folder could not be opened.", "無法開啟資料夾。"),

        ["Section.General"] = ("General", "一般"),
        ["General.Startup"] = ("Start with Windows", "開機時啟動"),
        ["General.StartupHint"] = ("Opens quietly in the tray when you sign in.", "登入 Windows 時在系統匣中安靜啟動。"),
        ["General.CloseButton"] = ("Close button", "關閉按鈕"),
        ["General.CloseButtonHint"] = ("What the window's X button does.", "視窗右上角 X 按鈕的作用。"),
        ["General.MinimizeToTray"] = ("Minimize to tray", "縮小到系統匣"),
        ["General.ExitApplication"] = ("Exit application", "結束應用程式"),
        ["General.Appearance"] = ("Appearance", "外觀"),
        ["General.AppearanceHint"] = ("Light or dark, for InstaDesktop and Instagram.", "InstaDesktop 與 Instagram 使用淺色或深色。"),
        ["General.ThemeSystem"] = ("Use Windows setting", "跟隨 Windows 設定"),
        ["General.ThemeDark"] = ("Dark", "深色"),
        ["General.ThemeLight"] = ("Light", "淺色"),
        ["General.Language"] = ("Language", "語言"),
        ["General.LanguageHint"] = ("For InstaDesktop's own menus and settings. Takes effect after a restart.", "InstaDesktop 本身的選單與設定所使用的語言，重新啟動後生效。"),
        ["General.LanguageSystem"] = ("Use Windows setting", "跟隨 Windows 設定"),
        ["General.RememberPage"] = ("Open where you left off", "開啟時回到上次的頁面"),
        ["General.RememberPageHint"] = ("InstaDesktop reopens on the page you used last, such as Messages, instead of Home.",
            "InstaDesktop 會回到上次使用的頁面（例如訊息），而不是首頁。"),

        ["Section.Notifications"] = ("Notifications", "通知"),
        ["Notif.Desktop"] = ("Desktop notifications", "桌面通知"),
        ["Notif.DesktopHint"] = ("Shows Instagram's messages and alerts as Windows notifications, even from the tray. To pause them for a while, right-click the tray icon.",
            "將 Instagram 的訊息與提醒顯示為 Windows 通知，縮小到系統匣時也會顯示。若要暫停一段時間，請在系統匣圖示上按右鍵。"),
        ["Notif.Sound"] = ("Notification sound", "通知音效"),
        ["Notif.SoundHint"] = ("Plays the Windows notification sound with each notification.", "每則通知都播放 Windows 通知音效。"),
        ["Notif.HideContent"] = ("Hide message content", "隱藏訊息內容"),
        ["Notif.HideContentHint"] = ("Notifications show who wrote, but not what they wrote or any image.", "通知只顯示是誰傳來的，不顯示內容和圖片。"),
        ["Notif.Flash"] = ("Flash the taskbar button", "閃爍工作列按鈕"),
        ["Notif.FlashHint"] = ("Flashes the taskbar button for a new message while InstaDesktop is open but not in front.",
            "InstaDesktop 已開啟但不在最前面時，有新訊息就閃爍工作列按鈕。"),
        ["Notif.Badge"] = ("Unread badge", "未讀徽章"),
        ["Notif.BadgeHint"] = ("Shows the number of unread messages on the taskbar button, the tray icon and the window title.",
            "在工作列按鈕、系統匣圖示和視窗標題顯示未讀訊息數量。"),
        ["Notif.OpenIn"] = ("Open message notifications in", "點訊息通知時開啟於"),
        ["Notif.OpenInHint"] = ("Where a clicked message notification shows its conversation.", "按下訊息通知時，要在哪裡顯示那個對話。"),
        ["Notif.OpenInMain"] = ("Main window", "主視窗"),
        ["Notif.OpenInPanel"] = ("Messages panel", "訊息面板"),
        ["Notif.Muted"] = ("Muted conversations", "已靜音的對話"),
        ["Notif.MutedNone"] = ("None. Choose Mute for 8 hours on a message notification to silence one conversation.",
            "沒有。在訊息通知上按「靜音 8 小時」即可讓單一對話不再通知。"),
        ["Notif.MutedSome"] = ("{0} muted. New messages from them do not show notifications.", "{0} 個對話已靜音，它們的新訊息不會顯示通知。"),
        ["Notif.UnmuteAll"] = ("Unmute all", "全部取消靜音"),

        ["Section.Shortcuts"] = ("Keyboard shortcuts", "鍵盤快捷鍵"),
        ["Shortcuts.Title"] = ("Keyboard shortcuts", "鍵盤快捷鍵"),
        ["Shortcuts.GlobalHint"] = ("These work from any app. Click a box and press the keys (Ctrl, Alt or Win plus a key); Backspace turns one off.",
            "這些快捷鍵在任何程式中都能使用。點一下方框後按下按鍵（Ctrl、Alt 或 Win 加一個鍵）；按 Backspace 可關閉。"),
        ["Shortcuts.Global"] = ("From any app", "在任何程式中"),
        ["Shortcuts.InApp"] = ("In InstaDesktop", "在 InstaDesktop 中"),
        ["Shortcuts.ShowWindow"] = ("Show or hide InstaDesktop", "顯示或隱藏 InstaDesktop"),
        ["Shortcuts.MessagesPanel"] = ("Show or hide the messages panel", "顯示或隱藏訊息面板"),
        ["Shortcuts.Sections"] = ("Home, Messages, Reels, Explore", "首頁、訊息、Reels、探索"),
        ["Shortcuts.Zoom"] = ("Zoom in or out (also Ctrl+mouse wheel)", "放大或縮小（也可用 Ctrl+滑鼠滾輪）"),
        ["Shortcuts.ZoomReset"] = ("Reset zoom", "重設縮放"),
        ["Shortcuts.Reload"] = ("Reload", "重新整理"),
        ["Shortcuts.BackForward"] = ("Back or forward", "上一頁或下一頁"),
        ["Shortcuts.Settings"] = ("Settings", "設定"),
        ["Shortcuts.ThisList"] = ("This list", "這份清單"),
        ["Shortcuts.DevTools"] = ("Developer tools (when on in Settings)", "開發人員工具（需在設定中開啟）"),
        ["Shortcuts.Off"] = ("Off", "關閉"),
        ["Shortcuts.Hint"] = ("Works even when InstaDesktop is in the tray.", "InstaDesktop 在系統匣中時也能使用。"),
        ["Shortcuts.SaveToApply"] = ("Save to use the new shortcut.", "按「儲存」後使用新的快捷鍵。"),
        ["Shortcuts.InUse"] = ("Another app already uses this shortcut. Choose different keys.", "其他程式已在使用這組快捷鍵，請換一組按鍵。"),
        ["Shortcuts.ViewAll"] = ("View all shortcuts (Ctrl+/)", "檢視所有快捷鍵 (Ctrl+/)"),

        ["Section.Calls"] = ("Calls", "通話"),
        ["Calls.Intro"] = ("Calls open in their own window. When a switch is on, Instagram can ask for the device and InstaDesktop asks you before the first use. Turning it off blocks requests and stops any use in progress immediately.",
            "通話會在獨立視窗中開啟。開關開啟時，Instagram 可以要求使用裝置，InstaDesktop 會在第一次使用前詢問你。關閉開關會封鎖要求，並立即停止正在進行的使用。"),
        ["Calls.Microphone"] = ("Allow microphone", "允許麥克風"),
        ["Calls.Camera"] = ("Allow camera", "允許相機"),
        ["Calls.OnTop"] = ("Keep call windows on top", "通話視窗保持最上層"),
        ["Calls.OnTopHint"] = ("Call windows stay above other windows. Each call window can also change this from its title bar menu (right-click the title).",
            "通話視窗會停留在其他視窗上方。每個通話視窗也可以從標題列選單（在標題上按右鍵）切換。"),
        ["Calls.WindowsPrivacy"] = ("Windows must also let desktop apps use them: Privacy & security > Microphone / Camera > Let desktop apps access.",
            "Windows 也必須允許桌面應用程式使用它們：隱私權與安全性 > 麥克風／相機 > 允許桌面應用程式存取。"),
        ["Calls.MicrophonePrivacy"] = ("Microphone privacy", "麥克風隱私權"),
        ["Calls.CameraPrivacy"] = ("Camera privacy", "相機隱私權"),
        ["Calls.ResetPermissions"] = ("Reset website permissions", "重設網站權限"),
        ["Media.Off"] = ("Off: Instagram calls cannot use it.", "已關閉：Instagram 通話無法使用。"),
        ["Media.AskNext"] = ("Instagram will ask on the next call.", "下次通話時 Instagram 會詢問。"),
        ["Media.Allowed"] = ("Allowed for Instagram.", "已允許 Instagram 使用。"),
        ["Media.Blocked"] = ("Blocked by your earlier answer. Turn this off and on (Save each time), or reset website permissions, to be asked again.",
            "因你先前的選擇而封鎖。將此選項關閉再開啟（每次都按儲存），或重設網站權限，即可再次詢問。"),
        ["Perm.Reset"] = ("Website permissions reset. Instagram will ask again when needed; a call in progress loses camera and microphone access.",
            "已重設網站權限。Instagram 需要時會再次詢問；進行中的通話會失去相機和麥克風的存取權。"),
        ["Perm.NotReady"] = ("Instagram is not loaded yet. Wait for it to load, then try again.", "Instagram 尚未載入。請等它載入完成後再試一次。"),
        ["Perm.Unsupported"] = ("Permission reset is unavailable. Update WebView2 Runtime and try again.", "無法重設權限。請更新 WebView2 Runtime 後再試一次。"),
        ["Perm.Failed"] = ("Website permissions could not be reset. Check the app log and try again.", "無法重設網站權限。請查看應用程式記錄後再試一次。"),

        ["Section.Downloads"] = ("Downloads", "下載"),
        ["Downloads.Folder"] = ("Download folder", "下載資料夾"),
        ["Downloads.WindowsDownloads"] = ("{0} (Windows Downloads)", "{0}（Windows 下載資料夾）"),
        ["Downloads.Change"] = ("Change folder...", "變更資料夾…"),
        ["Downloads.Open"] = ("Open folder", "開啟資料夾"),
        ["Downloads.Reset"] = ("Use Windows Downloads", "使用 Windows 下載資料夾"),
        ["Downloads.Ask"] = ("Ask where to save each download", "每次下載都詢問儲存位置"),
        ["Downloads.AskHint"] = ("When off, photos and videos you download are saved straight to the folder above.", "關閉時，下載的相片和影片會直接存到上方的資料夾。"),
        ["Downloads.ChooseTitle"] = ("Choose where InstaDesktop saves downloads", "選擇 InstaDesktop 儲存下載的位置"),

        ["Section.Updates"] = ("Updates", "更新"),
        ["Updates.Auto"] = ("Install updates automatically", "自動安裝更新"),
        ["Updates.AutoHint"] = ("Checks at startup and every 6 hours, and installs while the window is hidden and no call is open. When off, InstaDesktop tells you when a new version is available.",
            "在啟動時和每 6 小時檢查一次，並在視窗隱藏且沒有通話時安裝。關閉時，有新版本時 InstaDesktop 會通知你。"),
        ["Updates.Check"] = ("Check for updates", "檢查更新"),
        ["Updates.NotAvailable"] = ("Updates are not available in this mode.", "此模式無法使用更新。"),
        ["Updates.Checking"] = ("Checking...", "正在檢查…"),
        ["Updates.CheckFailed"] = ("Could not check for updates. Check your connection and try again.", "無法檢查更新。請檢查網路連線後再試一次。"),
        ["Updates.UpToDate"] = ("InstaDesktop {0} is up to date.", "InstaDesktop {0} 已是最新版本。"),
        ["Updates.Available"] = ("InstaDesktop {0} is available (you have {1}).", "InstaDesktop {0} 已推出（目前為 {1}）。"),
        ["Updates.InstallQuestion"] = ("Install InstaDesktop {0} now?\n\nInstaDesktop closes and restarts. Unsaved changes on this page are discarded.",
            "要現在安裝 InstaDesktop {0} 嗎？\n\nInstaDesktop 會關閉並重新啟動，此頁面未儲存的變更將會捨棄。"),
        ["Updates.DialogTitle"] = ("InstaDesktop update", "InstaDesktop 更新"),
        ["Updates.Downloading"] = ("Downloading...", "正在下載…"),
        ["Updates.InstallFailed"] = ("The update could not be installed. Try again later.", "無法安裝更新，請稍後再試。"),

        ["Section.Performance"] = ("Performance", "效能"),
        ["Perf.LowMemory"] = ("Background low-memory mode", "背景低記憶體模式"),
        ["Perf.LowMemoryHint"] = ("Uses less memory in the tray. Only applies while desktop notifications are off and no call is open.",
            "在系統匣中使用較少記憶體。只在桌面通知關閉且沒有通話時套用。"),
        ["Perf.Hardware"] = ("Hardware acceleration", "硬體加速"),
        ["Perf.HardwareHint"] = ("Uses the GPU for smoother video. Takes effect after InstaDesktop restarts.", "使用 GPU 讓影片更流暢。重新啟動 InstaDesktop 後生效。"),

        ["Section.Advanced"] = ("Advanced", "進階"),
        ["Adv.DevTools"] = ("Developer tools", "開發人員工具"),
        ["Adv.DevToolsHint"] = ("Enables F12 and Inspect for troubleshooting.", "啟用 F12 與「檢查」以進行疑難排解。"),
        ["Adv.ClearCache"] = ("Clear cache", "清除快取"),
        ["Adv.ClearCacheHint"] = ("Clearing the cache fixes pages that load incorrectly. You stay signed in.", "清除快取可修正載入異常的頁面，不會登出。"),
        ["Adv.CacheCleared"] = ("Cache cleared and Instagram reloaded. You are still signed in.", "已清除快取並重新載入 Instagram，你仍保持登入。"),
        ["Adv.CacheFailed"] = ("The cache could not be cleared. Check the app log and try again.", "無法清除快取。請查看應用程式記錄後再試一次。"),
        ["Adv.OpenLogs"] = ("Open logs", "開啟記錄"),
        ["Adv.Export"] = ("Export diagnostics...", "匯出診斷資料…"),
        ["Adv.ExportTitle"] = ("Save InstaDesktop diagnostics", "儲存 InstaDesktop 診斷資料"),
        ["Adv.ExportFilter"] = ("Zip archive (*.zip)|*.zip", "ZIP 壓縮檔 (*.zip)|*.zip"),
        ["Adv.Exported"] = ("Diagnostics saved. They contain versions, settings choices and the app log, without messages, names or page addresses.",
            "已儲存診斷資料。內容包含版本、設定選項與應用程式記錄，不含訊息、姓名或網址。"),
        ["Adv.ExportFailed"] = ("The diagnostics could not be saved. Choose another folder and try again.", "無法儲存診斷資料，請選擇其他資料夾後再試一次。"),
    };
}

// {l:T Key} in XAML: the text for the current language, resolved at load.
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;
    [ConstructorArgument("key")] public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
