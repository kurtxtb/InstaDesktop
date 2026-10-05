# InstaDesktop 驗證紀錄

## 訊息視窗、淺色／深色、繁體中文、單一對話靜音、隱藏內容、工作列閃爍與跳躍清單、清除快取（2026-10-06，v1.3.0，最新）

- Stable publish：smoke 全數通過，新增檢查：淺色／深色切換套用到 App 筆刷、Instagram 配色與 WebView 背景；Windows 語言判斷（zh-TW/HK/MO 才用繁中）與程式碼、XAML 的繁中文字；訊息視窗開啟、重複開啟沿用同一視窗、關閉記住大小；清除快取後重新載入且 localStorage 保留；跳躍清單 7 個項目、只接受固定指令、第二次啟動透過 named pipe 傳指令、Reels 指令實際導覽；工作列閃爍依設定與焦點；靜音只存有效對話並在重新啟動後保留；所有介面字串都有中英文。
- 通知 281/281（新增：對話 toast 有背景啟動的「靜音」按鈕、無對話的 toast 沒有；隱藏內容保留寄件者、去掉內文與圖片；新 toast 只宣告一次；靜音對話不顯示；按「靜音」只靜音不開視窗）；媒體 103/103；window layout 通過。
- 設定頁快照（繁中）：`artifacts/verification/settings-zh-light.png`、`settings-zh-dark.png`。
- 未實測：真實 toast 上按「靜音」（需要真實訊息通知）、實際工作列閃爍與跳躍清單的外觀（需要可見的桌面工作階段）、Windows 切換淺色／深色時的即時跟隨（以 `UserPreferenceChanged` 實作，未切換系統設定）。

## 未讀徽章、暫停通知、直接下載、通話置頂與更新選項（2026-10-06）

- Stable publish：smoke 全數通過，新增檢查：合成的「訊息」連結數字 → 工作列 overlay、視窗標題、系統匣圖示；關閉徽章設定；數字歸零清除；暫停／恢復通知與「明天早上 8:00」；下載資料夾套用到 profile 且 blob 下載不詢問直接存檔；通話視窗依設定置頂、標題列選單切換。
- 通知 275/275（新增：提示音關閉時 toast 為靜音；更新後通知只在升級時出現、連到正確的 release 頁）；媒體 103/103（一次中途 InvalidOperationException 未重現，之後連續 3 次通過）；window layout 通過。
- 設定頁以 `--settings-snapshot` 渲染完整頁面（快照改為輸出整個捲動內容）：`artifacts/verification/settings-features.png`；徽章圖：`stable-smoke.taskbar-badge.png`、`stable-smoke.tray-badge.png`。
- 未實測：真實 Instagram 帳號的未讀數（以合成 DOM 驗證，讀取方式與既有通知徽章相同）；關閉自動更新時的「有新版本」通知與從通知安裝（需要比目前更新的 GitHub Release）。

## 自動復原、更新、縮放與移除未使用的客製化層（2026-10-05）

- 環境：Windows 10 Pro 19045，.NET SDK 8.0.319（使用者目錄），WebView2 Runtime 154.0.4258.53，SDK 1.0.4191.47。
- Stable publish：smoke 全數通過（舊版長期失敗的「CSS and JavaScript injection」已隨客製化層移除，測試改為檢查實際出貨行為）、通知 270/270、媒體 103/103、window layout 通過；安裝程式可編譯。
- 新 smoke 檢查：設定部分更新不互相覆蓋、縮放值限制範圍、Ctrl+1~4 目標真的導覽頁面、快速上一頁/下一頁不被當成載入失敗、縮放放大/縮小/重設與儲存、全螢幕隱藏/恢復標題列、瀏覽器程序被終止後不按 Retry 自動恢復。
- 過程中發現並修正兩個既有問題：(1) 瀏覽器程序崩潰後讀取 `WebView2.CoreWebView2` 會丟例外，使重建（含 Retry 按鈕）永遠失敗；(2) 被較新導覽取代或由頁面中止的導覽（ConnectionAborted）被當成載入失敗而顯示錯誤畫面。
- 第二輪（版本 1.2.2）：smoke 另加「離線載入失敗後，網路恢復 3 秒即重試並載入」、「忙碌 3 秒的頁面在寬限期內不被替換、仍無回應才替換」、「深色 viewport 樣式於文件建立時套用」；Stable 與 Single-file smoke 皆通過，通知 270/270、媒體 103/103、window layout 通過，安裝程式可編譯。
- 安裝程式升級以隔離變體實測（不同 AppId、資料夾、mutex，無捷徑、無解除安裝時的通知清理，重新啟動改為記錄參數）：預先放入的舊 `Assets` 被刪除、`/BACKGROUND=1` 時重新啟動參數為 `--background`、未帶時為空、靜默解除安裝移除檔案與登錄。
- 仍未實測：Ctrl+滾輪縮放的儲存（DevTools 合成的 Ctrl+滾輪不會觸發瀏覽器縮放，需真實輸入）；硬體加速切換後的「立即重新啟動」（新執行個體使用真實 profile，未在本機執行）；睡眠喚醒觸發重試（與網路恢復共用同一排程路徑）。

## 獨立通話視窗與設定頁重新設計（2026-10-01）

- 實機紀錄 `NewWindowRequested Code=121` 證實 Instagram 以 www 網域、路徑含 call 的 popup 開啟通話；舊版把它導到主視窗。現在改為 SDK `NewWindow` 的獨立 `CallWindow`（同一 Environment/Profile，保留 `window.opener`）。
- 媒體測試擴充為 103 項（Stable publish 通過）：通話視窗開啟、主畫面不變、opener 雙向、共用權限流程、關閉開關停止通話視窗擷取（關閉鏡頭會結束同一次請求的所有 track，麥克風授權保留）、`window.close()` 只關通話視窗、等待詢問時關閉視窗、非通話與不可信 popup 維持原行為、服務釋放時關閉通話視窗；通話視窗全程使用離線 fixture。
- 通知 270/270、window layout 通過。設定頁以 `--settings-snapshot` 渲染檢查：`artifacts/verification/settings-redesign.png`。
- 仍未執行真實 Instagram 通話。

## 通話麥克風與鏡頭權限修正（2026-09-30）

- 環境：Windows 10 Pro 19045，.NET SDK 8.0.319（使用者目錄），WebView2 Runtime 154.0.4258.37，SDK 1.0.4191.47。
- `--media-permission-test <report.json>`（`scripts/verify-media-permissions.ps1`）：隔離 profile、離線 fixture、Chromium 虛擬擷取裝置（`--use-fake-device-for-media-stream`，僅限診斷 Environment，未使用 fake-UI）。報告含 Runtime 事實探測（`facts`）與 87 項 App 檢查。
- Runtime 事實：handler 回 Deny 且保存時，之後的請求不再觸發事件（舊版根因）；已保存 Allow 改為 Default/Deny 會結束進行中的 track，只允許單次請求、沒有保存的 Allow 不會；共用 profile 的隱藏 controller 在 profile 已保存 Allow 時不觸發事件即可擷取；委派給跨來源 iframe 的請求歸屬於頂層 origin；Low memory 不會結束 track。
- Stable publish：媒體 87/87、通知 270/270、window layout 通過；Single-file 媒體 87/87。連續三次開發建置媒體測試皆通過。
- 既有 smoke 測試在「CSS and JavaScript injection」失敗；以 HEAD `cbc81fa` 另行建置在相同 Runtime 下也在同一項失敗（`artifacts/verification/media-fix-head-baseline-smoke.json`），與本次修改無關。
- 報告：`artifacts/verification/media-permissions.json`、`media-permissions-single-file.json`、`media-fix-notifications.json`、`media-fix-window-layout.json`、`media-fix-smoke.json`。
- 未執行真實 Instagram 通話、真實裝置、Windows 隱私開關或 popup 通話流程；發行說明中的已知問題保留，直到手動驗收完成。

## 設定按鈕點擊修正（最新）

- 標題列設定按鈕加上 `WindowChrome.IsHitTestVisibleInChrome=True`，讓滑鼠事件交給按鈕，而不是視窗拖曳區。
- Release Build 與兩種 Publish 通過。因 `publish/win-x64` 舊版仍在執行，一般版新版輸出至 `publish/settings-fix/InstaDesktop.exe`；單檔版更新於 `publish/single-file/InstaDesktop.exe`。
- 兩種新版 EXE 在兩個螢幕的一般視窗、最大化、全螢幕返回狀態下，設定按鈕中心的原生 `WM_NCHITTEST` 均回傳 `HTCLIENT`；既有邊界檢查也通過。
- 報告：`artifacts/verification/settings-fix-settings-hit-test.json`、`artifacts/verification/single-file-settings-hit-test.json`。這是原生點擊區域測試，未自動操作正式登入視窗。

## 2026-09-07 底部白帶修正

- 移除最大化時額外的 7px 外距與主螢幕 MaxHeight 限制，視窗背景明確使用深色。
- WM_GETMINMAXINFO 使用目前螢幕的實體像素工作區；影片全螢幕使用完整螢幕範圍，返回時保持舊工具列與側欄收合。
- 修正單檔版啟動時的圖示 XAML 載入錯誤：將 app.ico 改為 WPF Resource，避免 Content pack URI 依賴單檔模式中不存在的 Assembly.Location；完整 Rebuild 後重新發佈。
- Release Build、Stable Publish、Single-file Publish 成功，0 warnings / 0 errors。
- 以 `--window-layout-test <report.json>` 在隔離設定下執行離線 WPF 邊界測試。兩個實際連接螢幕（2560×1440、1920×1080）均通過最大化、全螢幕、返回最大化與還原一般視窗測試。
- Build、Stable 與 Single-file EXE 均通過；Build 邊界報告為 `artifacts/verification/window-layout.json`；最終圖示修正後的發佈報告為 `artifacts/verification/win-x64-window-layout-final.json`、`artifacts/verification/single-file-window-layout-final.json`。
- 本次量測 WPF BrowserHost 的實際螢幕座標，未測試登入中的 Instagram 內容、實際媒體播放或混合 DPI。未重跑以下早期版本的網站功能 smoke suite，亦未更新 Installer。

以下為早期版本的驗證紀錄，不代表本次重新執行的結果。

驗證日期：2026-09-07，Asia/Taipei。

環境：Windows 11 x64，.NET SDK 10.0.302 建置 `net8.0-windows`；最終自包含輸出使用 .NET 8.0.29；WebView2 Evergreen Runtime 152.0.4191.66；WebView2 SDK 1.0.4191.47；Inno Setup 6.7.3。

## 實際執行結果

| 項目 | 結果 |
| --- | --- |
| 初始 WPF + WebView2 Release Build | 成功 |
| 最終 `dotnet build -c Release` | 成功，0 warnings / 0 errors |
| `dotnet publish -c Release -p:PublishProfile=Stable` | 成功，self-contained win-x64 |
| `dotnet publish -c Release -p:PublishProfile=SingleFile` | 成功，native self-extraction，trimming 關閉 |
| `build-release.bat --no-pause` | 成功 |
| `build-single-file.bat --no-pause` | 成功 |
| `build-installer.bat --no-pause` | 成功，產生 Installer EXE |
| 最終 Release EXE 實際啟動 | 31 個檢查通過 |
| 最終 Stable Publish EXE 實際啟動 | 31 個檢查通過 |
| 最終 Single-file EXE 實際啟動 | 31 個檢查通過 |
| 單獨複製 Single-file EXE 到空資料夾 | 31 個檢查通過；自動建立 CSS / JS defaults |

最後三份 EXE 測試完成於 15:32:48–15:32:51。單獨複製 EXE 的測試完成於 15:30:38，之後只有 dispatcher callback 寫法調整；最終 Single-file 另已完整重測。

最初 sandbox 內網路／WebView 子程序受限；正式驗證改用已核准的程序執行權限。過程中真實 Crash 測試發現恢復時嘗試 Focus 已失效的 WebView，已修正並重測通過。

## 驗證內容與證據

- 官方 Instagram HTTPS 登入頁成功載入，人工檢視 `artifacts/verification/release-smoke.png` 確認顯示正常。
- 正式網域、相似惡意網域、userinfo、HTTP、非標準 port、file/javascript 協定的路由規則。
- 外部頂層導航、新視窗轉交系統瀏覽器入口；測試模式截住 Shell 呼叫，不實際打開其他瀏覽器。
- 設定序列化／還原，Settings 視窗能建立。
- CSS / JS 注入、style 被移除後恢復、SPA 路由變更、Reload 後保留、關閉 customization 後不再注入。
- 最小化 Low、還原 Normal，Tray 隱藏後 JavaScript 仍能執行且 `IsSuspended` 為 false。
- 測試專用 localStorage marker 跨 Reload 與 controller 重建保留；未讀取 Cookies、帳密或登入 Session。
- 真正終止隔離 profile 的 WebView browser process，Crash UI 出現，Reload 成功重建並載入官方 Instagram。
- 單檔輸出的 WPF / WebView2Loader native dependency 可正常載入。

原始結果：

- `artifacts/verification/release-smoke.json`
- `artifacts/verification/stable-smoke.json`
- `artifacts/verification/single-file-smoke.json`
- `artifacts/verification/standalone-smoke.json`
- `artifacts/verification/outputs.json`（最終 EXE 大小与 SHA-256）

每次診斷建立獨立 `artifacts/verification/profile-*`，不使用 `%LOCALAPPDATA%/InstaDesktop/UserData`，不變更使用者開機啟動設定。測試結束後該 App 與 WebView controller 均已退出。

## 最終輸出

| 產物 | 路徑 | EXE 大小 |
| --- | --- | --- |
| Release | `bin/Release/net8.0-windows/win-x64/InstaDesktop.exe` | 173,056 bytes（app host，不是完整散布大小） |
| Stable | `publish/win-x64/InstaDesktop.exe` | 173,056 bytes（須保留整個資料夾） |
| Single-file | `publish/single-file/InstaDesktop.exe` | 162,925,809 bytes，約 155.4 MiB |
| Installer | `publish/installer/InstaDesktop-Setup.exe` | 51,273,388 bytes，約 48.9 MiB |

Single-file SHA-256：`E62EECC0100AF6F5D51AC1C3E94BF70C71BFBAC7B135242D7BBEC4D20BC4809E`

Installer SHA-256：`16CD04A489CF238B0835CF346B0F0D5AD8A3DFEEF1974594186D11BDEE92E858`

## 尚需人工驗收／限制

未使用 Instagram 帳號登入，未測試真實 DM 收發、通知、上傳／下載、Reels、相機／麥克風、長時間背景連線、Windows 開機啟動、實際鍵盤按鍵或多螢幕混合 DPI。這些功能保留官方 WebView 行為並有必要 handler，但不聲稱已經端到端驗證。

Installer 已編譯，沒有實際安裝／解除安裝或修改目前使用者的 startup registry。登入保持無法超越 Instagram 的 Session 到期與帳號驗證規則。跨 Facebook 的登入按需求開到外部瀏覽器，不能保證把登入結果帶回 WebView。

未做 Chrome / Edge 對照效能 benchmark。JSON 中 `appWorkingSetMB` 是測試時 App host 的瞬間 working set，**不包含所有 WebView2 子程序**，不能用它宣稱總 RAM 或固定節省比例。

通知保留 WebView2 原生 UI，但 Windows 設定、Instagram、Runtime、背景節流與網路狀態都會影響交付；没有自製 Windows Toast / persistent push 橋接。Emoji 圖片替換僅有模組擴充點，預設沒有變更輸入或傳送 Unicode。
