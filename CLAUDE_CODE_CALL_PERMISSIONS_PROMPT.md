# Claude Code 提示詞：修復 InstaDesktop 通話的麥克風與鏡頭權限

請在目前專案直接調查、實作及驗證 Instagram 通話的麥克風與鏡頭權限問題，不要只提出建議。先閱讀適用的 AGENTS.md，尊重現有未提交變更，以最小必要改動完成修復。回覆使用繁體中文，應用程式介面沿用現有英文。

## 專案與目標

- 專案：`C:\Users\Cody\Documents\Project\InstaDesktop`。
- 分析基準：2026-09-30，HEAD `cbc81fa`，版本 1.1.0。實作前重新確認 HEAD 與檔案，以下行號可能隨修改移動。
- 技術：C#、.NET 8、WPF、Windows 10 1809+/Windows 11 x64、WebView2 SDK `1.0.4191.47`、Evergreen Runtime，未封裝桌面程式。
- Instagram 網頁負責實際通話；目前沒有自行實作的 WebRTC 引擎或通話狀態管理。
- 目標：使用者明確允許後，可以在 Instagram 原生介面進行語音／視訊通話；拒絕、重新允許、重設、重啟及撤銷授權都具有一致、可理解、可驗證的行為。
- 必須區分應用程式設定、WebView2 網站授權、Windows 隱私設定、裝置／驅動狀態與 Instagram 本身的通話流程。網站授權成功不等於作業系統已准許，也不等於通話成功。

## 已確認的程式事實

以下是靜態檢查及本機 SDK 文件確認的事實，並非已執行真實通話的結果。

1. `Models/AppSettings.cs:17-18`：`AllowMicrophone`、`AllowCamera` 沒有初始化值，預設皆為 false；舊設定 JSON 缺少欄位時也會得到 false。
2. `SettingsWindow.xaml:26-27`：只有 `Allow microphone`、`Allow camera` 核取方塊。
3. `Services/WebViewService.cs:312-359`：`PermissionRequested` 先檢查 `NavigationPolicy.IsTrusted(e.Uri)`。設定 false 時，麥克風／鏡頭立即 Deny 並 return，沒有向使用者說明被應用程式擋下。
4. 上述立即拒絕分支沒有設定 `SavesInProfile = false`。本機 SDK XML 明載：權限決定預設儲存於 Profile，跨工作階段保留；瀏覽器啟發式規則會影響後續是否再次觸發事件。因此應用程式關閉開關造成的 Deny 會有持久化問題，重新開啟設定後是否再次進入 handler 必須用真實 Runtime 驗證。
5. 設定 true 時不是直接 Allow，而是取得 deferral，透過 Dispatcher 顯示 Yes/No MessageBox，再將結果存入 Profile (`WebViewService.cs:333-357`)。因此目前是「應用程式開關＋網站二次詢問」兩層同意。
6. `SettingsWindow.xaml.cs:59-60,76-78` 儲存開關後呼叫 `ApplySettingsAsync`；但 `WebViewService.cs:564-583` 只同步 Notifications，沒有修復／同步 Microphone、Camera 的舊 Profile 狀態，也沒有主動處理已運作的媒體串流。
7. `WebViewService.cs:585-596` 重設所有可信 Instagram origin 的非 Default 網站授權，接著只重新套用通知設定。重設不會把兩個應用程式開關設為 true；在 false 狀態下，下一次請求仍會立即 Deny。Core 不存在時方法直接 return，但設定視窗仍會顯示重設成功。
8. `Services/NavigationPolicy.cs:10-16` 的導覽信任規則接受所有 HTTPS、預設埠、無 userinfo 的 `instagram.com` 子網域。麥克風／鏡頭沿用此較廣泛的導覽規則，沒有獨立的媒體 origin 政策。
9. `Services/DirectInboxMonitor.cs:57-60` 隱藏通知 WebView 與主視窗共用 Environment／Profile；`:148-154` 透過 `NotificationPermissionPolicy.ForMonitor` 拒絕非通知權限，且不保存拒絕。這個不污染 Profile 的設計必須維持。
10. `WebViewService.cs:299-309` 對所有新視窗設 `Handled = true`；可信 URL 改在目前主 WebView 導覽，外部 URL 交給系統瀏覽器；沒有將新的 WebView 指派給 `e.NewWindow`。
11. `WebViewService.cs:500-519` 在背景、低記憶體開啟且通知關閉時設 Low，沒有媒體擷取／通話狀態判斷。程式沒有呼叫 TrySuspendAsync。SDK 明載 Low 不會停止 JavaScript，只可能影響效能；不可直接聲稱它已造成斷線。
12. 全專案搜尋沒有找到 `getUserMedia`、`RTCPeerConnection`、`mediaDevices`、`permissions.query` 的通話診斷或媒體測試，也沒有 Windows camera/microphone 隱私設定入口。
13. 現有 Diagnostics 的相關測試集中於 Notifications；`NotificationMonitorPermissionTests.cs:102-106` 有媒體種類的政策函式 Deny 測試，沒有主視窗真實媒體請求／保存／撤銷的整合測試。
14. `.github/release-notes/v1.1.0.md:54` 明確列出已知問題：`Microphone and camera are inaccessible during calls (unchanged from v1.0.0).` 這證明問題已有記錄，不能單憑此句斷定根因。
15. `MainWindow.xaml.cs:55-58` 在建構時關閉 UI customization。預設流程不是靠 custom.css 或 inject.js 實作通話，沒有證據支持優先改 CSS。

## 本機 SDK 依據

優先查閱目前已安裝版本，不要憑記憶發明 API：

`.nuget/packages/microsoft.web.webview2/1.0.4191.47/lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.Core.xml`

- `CoreWebView2PermissionRequestedEventArgs.SavesInProfile`：預設保存決定；設 false 才不跨請求保留，並繼續接收此 origin／kind 的請求事件。
- `CoreWebView2Profile.SetPermissionStateAsync`：持久化網站狀態；設 Default 可清除先前保存的狀態。
- `CoreWebView2Frame.PermissionRequested`：iframe 事件會先發生，再傳到 CoreWebView2；只有 frame handler 設 Handled 才阻止上層收到。不能把「沒有 FrameCreated handler」直接判定為漏掉所有 iframe 請求。
- `CoreWebView2NewWindowRequestedEventArgs.NewWindow`：正確提供 popup WebView 可保留 WindowProxy；要求與 opener 相同 Environment／Profile，且目標不能事先導覽。
- `CoreWebView2.MemoryUsageTargetLevel`：Low 是記憶體效能策略，不等同 Suspend。

## 實作要求

### 1. 先確認根因與授權語意

- 畫清楚目前流程：設定載入 → 初始化 Profile → 通話請求 → 網站授權 → OS／裝置結果 → 儲存／撤銷。
- 優先重現「設定 false 時拒絕被保存 → 勾選 true → 舊 Deny 仍在」及「網站曾 Allow → 關閉開關 → 舊 Allow／既有串流是否仍有效」。不要假設每次請求都會觸發 PermissionRequested。
- 保留既有使用者選擇及預設關閉的隱私語意，不要透過將所有預設改成 true 掩蓋問題。
- 若保留二次詢問，介面須讓使用者明白開關的效果是允許網站提出授權要求。處理應用程式阻擋時，提供低干擾且可操作的提示／設定入口，避免每次請求都跳重複視窗。
- 明確定義「使用者主動重新開啟」及「重設網站權限」如何恢復詢問。不能每次啟動都無條件清除使用者手動做出的網站 Deny。
- 歷史 Profile 的 Deny 沒有記錄來源，無法直接區分應用程式自動拒絕與使用者明確拒絕；提出並實作保守的遷移／明確恢復流程，記錄此限制。

### 2. 修正媒體政策與 Profile 狀態

- 建立與現有風格一致、可測試的媒體決策邏輯；不要修改整體導覽規則來達成媒體授權。
- 媒體授權先以精確 HTTPS origins `https://www.instagram.com`、`https://instagram.com` 為基礎。若真實通話需要其他 origin，先取得可去識別化的證據，再個別擴充；不要放行所有 Meta／Facebook／Instagram 子網域。
- 應用程式開關造成的單次拒絕要明確設定保存策略。啟動、開關變更、恢復詢問與重設流程必須共同確保最終狀態一致，不能只在 handler 裡加一個 Allow。
- 正確處理快速切換、非同步 Profile 寫入、重設與切換重疊及兩個 origin，防止較舊的操作覆寫使用者最後的選擇。
- 必須驗證關閉開關時既有 Allow、目前串流及新請求的結果。更新 Profile 不保證立即停止已取得的 MediaStream；先確認 Runtime 能力，再選擇可驗證的停止／安全重載方案。
- 若必須重載中止目前通話，提供明確提示，避免儲存不相關設定也重載頁面。不要以新建整套通話引擎或全面 monkey-patch getUserMedia 作為預設方案。
- 重設方法要能回報未初始化／不支援／失敗，避免 UI 假報成功；重設後持續遵守開關並保留通知權限同步。

### 3. 保護共享 Profile 的隱藏通知頁

- 隱藏 monitor 必須繼續不詢問、不授予媒體權限，也不能持久化 Deny 污染主頁。
- 實際驗證「主頁／Profile 已保存媒體 Allow」時，隱藏頁仍不能開始媒體擷取。既有單元測試只檢查 policy 回傳值，不足以證明 Runtime 在保存授權時仍會觸發 handler。
- 若確認共用授權可繞過 monitor 的事件拒絕，使用可支援、最小影響的方法強制阻止隱藏頁擷取，保持通知與登入共享需求；不要僅以 `IsMuted = true` 當作禁止麥克風，該屬性只控制音訊播放。

### 4. 整理權限對話框的生命週期

- 現有 Dispatcher＋deferral 是避免 COM callback 重入的合理方向，保留這個原則。
- 在延後詢問及採用答案時，重新驗證設定、request origin、目前 Core／文件生命週期；已關閉開關或已替換 WebView 的舊請求不得再被 Allow。
- 處理同時請求音訊與視訊、重複請求、導航、renderer crash、重建 Core、應用程式關閉、Dispatcher 無法排程及例外。
- 每個成功取得的 deferral 都必須有明確且安全的完成路徑，避免重複完成或存取已釋放的 COM 物件。發生錯誤時採明確拒絕，不能留下 Default 而意外落回另一個授權 UI。
- 不要對所有權限種類一律自動 Allow，也不要把 `IsUserInitiated` 當成所有合法通話的硬性必要條件；非同步接聽流程可能沒有該旗標。

### 5. 有證據才調整 popup 與背景策略

- 檢查實際 Instagram 通話是同頁、iframe 還是 `window.open`，以及是否依賴 opener／WindowProxy／關閉子視窗行為。
- 目前可信 popup 被改成主頁導覽，確實沒有原本的新視窗語意；但尚未證明 Instagram 通話使用此路徑。先重現，再決定是否需支援專用通話子視窗。
- 若需要，依 SDK 正確完成 NewWindowRequested deferral、相同 Environment／Profile、權限／來源限制及子視窗釋放；不可改成主頁 Navigate 後宣稱等同 popup。
- 檢查通話期間縮小／收進 tray，在通知開／關時都能維持音訊與視訊。不要把低記憶體策略直接當成已確認根因，也不要用「允許媒體權限」推論「正在通話」。
- 如需媒體狀態偵測，選用目前 SDK 支援或最小範圍的方案，處理通話結束／track ended／導航／復原時清理。若缺乏可信訊號，說明保守策略的取捨。

### 6. 加入可用的診斷與 OS 引導

- 使用現有 LoggingService 固定事件名稱、數字代碼、例外型別／HResult，記錄媒體種類、主頁／monitor、允許／拒絕原因、是否保存及固定 origin 分類。
- 不記錄帳號、通話對象、完整 URL／query、訊息、cookies、token、音訊、影像、裝置標籤／ID 或任意 JS 例外訊息。
- 在隔離測試／診斷中區分 NotAllowedError、NotFoundError、NotReadableError、OverconstrainedError 等標準錯誤名稱；僅憑名稱不能武斷認定是 OS、網站或裝置哪一層阻擋。
- 提供 Windows 麥克風／鏡頭隱私設定的受限入口及可操作文字，說明桌面應用程式存取設定。不要為了繞過問題修改登錄檔、要求系統管理員權限或替未封裝 WPF 加上無效的 MSIX capability。

## 測試與驗收

沿用 `Diagnostics/`、`AppPaths.UseDiagnosticRoot`、離線 WebResourceRequested fixtures 及現有報告模式。自動測試不得存取真實登入 Profile 或自動撥打真實帳號。

至少覆蓋：

1. 預設／開關 false：拒絕且無重複對話框；應用程式拒絕不造成無法恢復的保存狀態。
2. false → true：含歷史 Deny 的 Profile 能依明確恢復流程重新詢問及允許；www／root origins 都驗證。
3. true → false：含歷史 Allow，測新請求及現有串流是否確實停止／被阻擋；不得只斷言設定值已改。
4. 明確按 No、重啟、再次請求、主動恢復、網站權限重設：符合定義的保存策略。
5. 麥克風／鏡頭各自開關，以及 `audio:true, video:true` 的組合請求；不能在鏡頭被拒時把已允許的語音政策一起誤判。
6. 同時請求、等待期間切換開關、導航、重建 WebView、renderer crash／關閉：無掛起、失效 Allow、重複完成或 COM 存取錯誤。
7. 不可信來源、非預設埠、userinfo、相似網域、未核准子網域及必要 iframe 路徑：遵守媒體 origin 政策。
8. 隱藏 monitor 在 Profile 未授權和已保存 Allow 的情況：均不能取得媒體；不污染主頁，通知仍正常。
9. popup 流程若被證實相關：保留 opener／子視窗生命週期與權限一致性。
10. 前景／最小化／tray，通知開／關：驗證所選背景策略；保留現有通知回歸檢查。

有必要時，可在隔離診斷 Environment 使用 Chromium 支援的虛擬媒體裝置，驗證真實 getUserMedia、track lifecycle 與 Profile 行為。不可把 fake-permission／fake-UI 旗標拿來代替真實授權流程驗證，也不可加入正式 Environment。若 Runtime 不支援所選測試方式，明確標示限制。

真實裝置／OS／Instagram 通話的手動驗收另列清單：

- 語音通話、視訊通話、來電接聽、通話中開關鏡頭及麥克風。
- 首次授權、曾拒絕後恢復、退出重啟、重設網站權限。
- Windows 隱私設定禁止／恢復桌面應用程式使用麥克風或鏡頭。
- 無裝置、裝置被占用、熱插拔及不同裝置。
- 最小化、tray、通知開／關、通話結束後裝置釋放。

執行適合改動的建置與現有回歸測試，例如 `build-release.bat --no-pause`、`scripts/verify.ps1`、`scripts/verify-notifications.ps1`；先檢查腳本及參數。測試必須使用本次建置的輸出，不能以舊 publish EXE 通過當成本次修復驗證。Windows 10／11 與 Single-file 相關流程按實際影響範圍驗證。

## 完成時交付

- 按嚴重程度列根因與檔案／行號，分清楚已重現問題、SDK 行為與尚未驗證假設。
- 說明最終授權策略、歷史狀態恢復方式，以及現有串流的撤銷行為。
- 提供實作差異、實際測試命令、結果／報告位置及未執行的手動案例。
- 只有完成真實通話驗證後才移除發行說明中的已知問題；離線虛擬裝置成功只能證明本機權限流程。
- 建置不等於更新使用者目前安裝的 EXE。交付可測試的輸出路徑，不要自行發布 Release 或覆蓋正式安裝。
- 保留登入資料、通知行為、更新功能及現有原生 Instagram UI；不要透過停用安全檢查或清空 UserData 解決問題。

請先讀程式與 SDK、重現優先問題，再完成最小修復與驗證。若真實通話需要使用者操作，先完成可自動完成的工作，最後給出精確的手動驗收步驟，並如實標示尚未證明的部分。
