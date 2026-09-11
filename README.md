# InstaDesktop

以 **C# / .NET 8 / WPF / Microsoft Edge WebView2** 製作的 Windows Instagram 桌面容器。單一專案，直接載入 <https://www.instagram.com/>，沒有 Electron、Node runtime、自製 API Client 或 Instagram 私有 API。

## Requirements

- Windows 10 / Windows 11，x64（ARM64 模擬執行不在本次驗證範圍）。
- 執行：Microsoft Edge WebView2 **Evergreen Runtime**。[官方下載頁](https://developer.microsoft.com/microsoft-edge/webview2/)。即使是自包含或 Single-file EXE，也需要另外存在這個瀏覽器 Runtime。
- 建置：.NET SDK 8.0.300 或更新版本。`global.json` 允許使用較新的正式 SDK，目標框架仍為 `net8.0-windows`。
- 首次 Restore / Publish 需要網路連線到 NuGet。WebView2 套件版本固定為 `1.0.4191.47`。
- 選用 Installer：Inno Setup 6（使用支援 `x64compatible` 的版本）。

## Run

建議直接執行 `publish\win-x64\InstaDesktop.exe`，或安裝 `publish\installer\InstaDesktop-Setup.exe`。

穩定版為自包含發佈，使用者不需要安裝 .NET；請保留整個 `publish\win-x64` 資料夾。Single-file 版在 `publish\single-file\InstaDesktop.exe`，可單獨複製 EXE，首次執行會嘗試在旁邊建立可編輯的 `Assets`。如果所在目錄不可寫入，則使用內嵌預設資源。

第一次啟動直接顯示 Instagram 官方登入頁。請在該頁手動登入。本程式不讀取、記錄或自行保存帳號密碼。一般情況下 WebView2 會保留登入狀態，但 Instagram 的 Session 到期、官方要求重新驗證、主動登出或刪除 profile 都可能使你需要重新登入，不能保證永久登入。

關閉視窗 X 預設隱藏到 Tray；**Tray → Exit** 才會完整退出。Windows 系統匣可能把圖示收在「顯示隱藏的圖示」中。重複啟動 EXE 會喚回既有視窗，不建立第二份登入 profile。Windows 關機／登出時會正常結束。

## Build

雙擊 `build-release.bat`，依序執行 Restore、Release Build 與 win-x64 自包含 Publish；遇到失敗立即停止。命令列／CI 可使用 `build-release.bat --no-pause`。

等價命令（在專案根目錄執行）：

```powershell
dotnet restore InstaDesktop.csproj
dotnet build InstaDesktop.csproj -c Release
dotnet run --project InstaDesktop.csproj -c Release
```

Release 輸出：`bin\Release\net8.0-windows\win-x64\InstaDesktop.exe`。一般 Build 是 framework-dependent，需安裝 .NET 8 Desktop Runtime；請以 Publish 版本散布。

批次檔把 NuGet cache 與 CLI home 放在專案內的 `.nuget`、`.build`，避免依賴開發者私有快取。`packages.lock.json` 記錄解析版本；不同 Publish 設定可能更新 SDK 提供的 bundler 相依項。

## Publish

```powershell
# 建議的穩定資料夾版本
dotnet publish InstaDesktop.csproj -c Release -p:PublishProfile=Stable

# Single-file 版本（或雙擊 build-single-file.bat）
dotnet publish InstaDesktop.csproj -c Release -p:PublishProfile=SingleFile
```

| 發佈形式 | 輸出 | 說明 |
| --- | --- | --- |
| Stable | `publish\win-x64\InstaDesktop.exe` | 自包含 EXE + 必要 DLL + Assets，優先建議 |
| Single-file | `publish\single-file\InstaDesktop.exe` | 自包含、內嵌 native library 與預設 Assets；可編輯資源另附 |
| Installer | `publish\installer\InstaDesktop-Setup.exe` | 免管理員權限的安裝程式 |

Single-file 使用 `PublishSingleFile=true`、`SelfContained=true`、`IncludeNativeLibrariesForSelfExtract=true`。WPF 與 WebView2 native library 會由 .NET 在啟動時解壓到使用者的暫存 bundle 目錄。兩種發佈都**不啟用 trimming**。Self-contained 體積包含 .NET Desktop Runtime，因此磁碟大小不等於執行時 RAM。

本機本次 Publish 採用 .NET **8.0.29**；使用其他 SDK 重建時，應使用最新修補版本並重新驗證。[Microsoft single-file 文件](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)。

## Installer

先執行 `build-release.bat`，再執行 `build-installer.bat`。若 Inno Setup 裝在其他目錄，將 `ISCC_PATH` 指向 `ISCC.exe`。

重新執行 `publish\\installer\\InstaDesktop-Setup.exe` 會自動沿用既有安裝資料夾並進行就地更新；安裝器會從發佈 EXE 讀取版本號，確保新版本能被正確辨識。使用者自訂的 `Assets` 檔案會保留。

若要讓程式啟動時自動下載更新，設定 `INSTA_UPDATE_MANIFEST_URL` 環境變數，或在 EXE 同目錄放置 `UpdateManifestUrl.txt`（必須是 HTTPS）。Manifest 格式為 `{ "version": "1.0.1", "installerUrl": "https://example.com/InstaDesktop-Setup.exe", "sha256": "..." }`；偵測到較新版本後會下載安裝器、驗證 SHA-256（若提供）、靜默安裝並重新啟動。

- 安裝到 `%LOCALAPPDATA%\Programs\InstaDesktop`，不要求 Administrator。
- 建立 Start Menu shortcut，Desktop shortcut 由使用者勾選。
- 更新安裝時保留已存在的自訂 CSS／JS，因此若要取得新版預設資源，請自行備份並更新 `Assets`。
- 解除安裝預設保留 `%LOCALAPPDATA%\InstaDesktop`，最後可明確選擇刪除登入資料、設定與日誌。Silent uninstall 一律保留。
- Installer 會移除指向該安裝目錄的開機啟動註冊。
- Installer 不內嵌 WebView2 Runtime；缺少時 App 提供官方 Runtime 下載按鈕。
- EXE 與 Installer 尚未做商業憑證簽章。

## Desktop controls / Settings

沒有地址列或瀏覽器 Toolbar，保留正常 Windows 標題列、最小化、最大化、關閉與調整大小。視窗大小／位置／最大化狀態會保存；移除螢幕後會把視窗放回可見工作區。一般最小尺寸 900 × 600，在工作區更小時會縮小下限以維持可操作。

| 操作 | 快捷鍵／入口 |
| --- | --- |
| 上一頁 / 下一頁 | Alt + Left / Alt + Right |
| Reload，重新讀取自訂檔案 | Ctrl + R / F5 |
| Settings | Ctrl + , 或 Tray → Settings |
| DevTools | 先在 Settings 啟用，再按 F12 |
| 喚回視窗 | 雙擊 Tray 或 Open Instagram |
| 完全退出 | Tray → Exit |

| 設定 | 預設 | 套用 |
| --- | --- | --- |
| Start with Windows | Off | 儲存後寫入目前使用者的 HKCU Run，啟動時進 Tray |
| Close button | Minimize to tray | 立即；可改為 Exit application |
| Hardware acceleration | On | Tray → Exit 後重新啟動 |
| Developer tools | Off | 立即；F12 |
| UI customization | On | 儲存後重載目前網頁 |
| Background low-memory mode | On | 最小化／Tray 時 Low，恢復 Normal |

開機啟動使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\InstaDesktop`，命令為加引號的 EXE 完整路徑與 `--background`。請從要長期使用的安裝／發佈目錄啟用此設定；移動 EXE 後重新儲存設定可修正路徑。沒有服務、排程輪詢或管理員需求。

## User data / settings / logs

```text
%LOCALAPPDATA%\InstaDesktop\
  UserData\       WebView2 官方管理的 Cookies / Session / cache
  settings.json   App 設定與視窗位置
  Logs\app.log    有上限的 App 事件日誌
  Logs\app.log.1  上一份日誌
```

日誌單檔上限約 512 KiB，共保留兩份，只記錄固定事件、例外類別、HResult 或數字錯誤碼。不記錄網址、例外訊息、網頁內容、Cookie、密碼、帳號 token 或 DM。沒有 request/response 攔截或帳號資料匯出。

Settings → Reset website permissions 只重設 Instagram 的相機／麥克風／通知等網站權限，不清除登入資料。勿在程式仍執行時手動改動 UserData。

## Edit custom.css

修改**目前執行的 EXE 旁**的 `Assets\Styles\custom.css`，然後 Ctrl + R / F5，或 Tray → Reload customization。不需要重新編譯。若修改原始碼目錄的 Assets，則重新 Build / Publish 會複製它們到輸出目錄。

預設只調整 scrollbar 並提供寬螢幕變數；刻意不覆寫 Instagram 不穩定的私有 class 或強制 Feed／DM 寬度，避免破壞上傳、媒體與對話框。可從以下安全的顏色調整開始：

```css
:root[data-instadesktop] {
    --instadesktop-scrollbar-thumb: #ad7a9caa;
}
```

如果要增加 16:9 / 21:9 或 DM 版面規則，先用 DevTools 確認目前 Instagram 的 DOM，限制選擇器作用範圍，再逐項驗證。關閉 UI customization 會重載並完全移除本次文件中的自訂。

## Edit inject.js

修改 `Assets\Scripts\inject.js`，Ctrl + R / F5 重新讀取。

- `initialize()`：初始化模組與 observer。
- `applyTweaks()`：應保持 idempotent；優先使用 CSS，避免反覆改寫相同 DOM。
- `observeInstagram()`：限定 main／根容器範圍；main 的 childList/subtree 變動用單一 250 ms debounce callback 合併，不監控 attributes / characterData。
- 背景時斷開自訂 observer，回前景時再套用。這只停止本程式的 UI tweak 監控，不停止 Instagram 的 scripts。
- `AddScriptToExecuteOnDocumentCreatedAsync` 處理整頁 Reload，WebView2 `SourceChanged` 與導航事件處理 SPA；React DOM 變化由 observer 處理。
- head 中的 style 被移除時會重新建立。沒有 setInterval、整頁固定輪詢或 History API 替換。
- 腳本只在最上層 `https://instagram.com` / `https://*.instagram.com`、標準 HTTPS port 注入，排除 iframe 與相似惡意網域。
- 語法錯誤或初始化失敗會記錄固定 InjectionError，並用 Tray 提示；Instagram 仍可使用。只接受一個固定的 injection-error web message，沒有 host object 或原生命令橋接。

內嵌預設檔案用於單獨複製 EXE；存在的外部檔案總是優先。你寫入的 JS 會在登入中的頁面執行，應只放自己信任的 UI 修改。

`Assets\Scripts\modules\emoji.js` 提供 `initialize / applyTweaks / dispose / displayText` 擴充點。第一版是 no-op，不提供自製 picker、不替換輸入內容；😂 等 Emoji 仍交由 Instagram 傳送原本的 Unicode，未實作圖片替換。

## Resource behavior

最小化／隱藏到 Tray 時切換 `MemoryUsageTargetLevel.Low`，恢復時切回 `Normal`。**沒有 TrySuspendAsync / Resume 呼叫**。不支援該 API 的 Runtime 會正常降級，Settings 會顯示狀態。

這是 best-effort 記憶體目標，腳本與連線繼續執行，不代表固定 RAM 上限。Windows / Chromium / Instagram 仍可能節流背景工作；無法保證通知即時抵達，睡眠／網路中斷時也無法保持連線。[Microsoft 低記憶體 API 說明](https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.memoryusagetargetlevel)。

App 沒有週期 Timer、常駐輪詢或空轉 thread。單一 instance 的喚醒使用 OS event；設定在操作時保存。JS 的單次 debounce 與診斷模式的 timeout 都由事件觸發。GPU 預設開啟；關閉選項使用 `--disable-gpu`，需完整重啟。

WebView2 仍然使用多個 Chromium 程序。相較完整瀏覽器，省去完整瀏覽器 UI 與額外分頁／擴充功能，但**不保證任何情境下 RAM、CPU 或 GPU 一定比較低**。要比較資源需以相同帳號、內容、時間與完整 WebView2 process tree 測量。

## Navigation, notifications, media

- HTTPS Instagram 正式網域留在 App，Instagram HTTP 連結升級 HTTPS。
- 外部 HTTP/HTTPS 連結與新視窗交給 Windows 預設瀏覽器。不自動啟動 file/javascript/data/自訂協定。
- **Facebook 登入／跨站驗證限制**：依需求，facebook.com 一律交給預設瀏覽器，其 Session 不會自動回傳這個 WebView。請優先在 Instagram 頁面直接登入；需跨站流程的功能不能保證完成。
- 檔案上傳沿用 HTML file picker；下載提供 Windows SaveFileDialog，再保留 WebView2 進度 UI。
- 相機、麥克風、通知等權限使用原生提示，只能由可信的 Instagram HTTPS origin 請求。其他來源拒絕；不預先核准全部權限。
- 通知沿用 WebView2 預設 UI，沒有攔截通知內容或另建 Toast 系統。預設通知行為與 [NotificationReceived.Handled 官方說明](https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2notificationreceivedeventargs.handled) 一致。
- 不保證 Service Worker persistent push、背景 DM 提醒、Instagram Web 不提供的功能或跨站登入；這些由 WebView2 Runtime、Windows 通知設定與 Instagram 控制。
- 未關閉憑證驗證、web security，未修改 User-Agent，未使用私有 API、GraphQL 端點、手機模擬或自動化互動。

## Crash / troubleshooting

Renderer crash、browser process 結束或 renderer 無回應時，顯示 `Instagram renderer crashed.` 與 `Reload Instagram`。重載會重建 WebView controller，保留既有 UserData；GPU／utility crash 交由 WebView2 自動恢復。

WebView 初始化有合理 timeout。找不到 Runtime 時顯示 `Microsoft Edge WebView2 Runtime is required.` 並提供官方下載頁按鈕；其他初始化錯誤提供重試及簡化日誌。網路錯誤也提供 Reload。

若白畫面或自訂失效，先關閉 UI customization，再重載；若仍失敗，查看 Settings → Open logs，更新 Evergreen Runtime，最後 Tray → Exit 後重開。

## Verification

本專案已實際執行 `dotnet build`、兩種 `dotnet publish`，並啟動輸出的 EXE 驗證。最新詳細結果見 `VERIFICATION.md` 與 `artifacts\verification\*.json`。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1 `
  -Exe 'publish\single-file\InstaDesktop.exe' `
  -Report 'artifacts\verification\single-file-smoke.json'
```

測試需要圖形桌面、WebView2 Runtime 與 Instagram 網路連線，使用 `--smoke-test <report-path>` 建立獨立測試 profile，不使用正式登入資料，不修改 Windows startup。測試包含真正關閉該測試專用 WebView browser process，再驗證 Crash → Reload。外部網址透過診斷攔截點驗證路由，不實際開啟其他瀏覽器。

測試不登入、不發送訊息，也不讀取任何密碼／Cookie／Instagram 內容。Profile 持久化以獨立測試 localStorage marker 驗證，並不宣稱驗證了已登入 Session。測試產生未登入頁面的 PNG，與此 profile 的診斷資料保留在 artifacts，可自行移除。

需人工登入後驗收：正常登入／再次啟動、DM 收發、長時間背景訊息、通知點擊、Reels、圖片／影片上傳、下載、麥克風／相機、Windows startup 與多螢幕混合 DPI。功能由官方網頁處理，未以帳號代為測試。

## Project structure

```text
InstaDesktop.csproj / global.json / packages.lock.json
App.xaml / App.xaml.cs
MainWindow.xaml / MainWindow.xaml.cs
SettingsWindow.xaml / SettingsWindow.xaml.cs
app.manifest
Models/AppSettings.cs
Services/
  AppPaths.cs, SettingsService.cs, StartupService.cs, LoggingService.cs
  NavigationPolicy.cs, ShellService.cs, WebViewService.cs, InjectionService.cs
Assets/
  Scripts/inject.js
  Scripts/modules/emoji.js
  Styles/custom.css
  Icons/app.ico
Properties/PublishProfiles/Stable.pubxml, SingleFile.pubxml
Diagnostics/SmokeTestRunner.cs
scripts/generate-icon.ps1, verify.ps1
installer/InstaDesktop.iss
build-release.bat / build-single-file.bat / build-installer.bat
README.md / VERIFICATION.md
```

這是非官方個人桌面容器，Instagram 名稱及其頁面內容屬原權利人。自訂 App 圖示由專案腳本產生。
