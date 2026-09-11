# InstaDesktop 驗證紀錄

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
