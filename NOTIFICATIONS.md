# Instagram notification implementation

## Architecture and compatibility

The application remains unpackaged, x64, .NET 8 WPF with WebView2 SDK 1.0.4191.47 and the existing Evergreen runtime/profile. The target is now `net8.0-windows10.0.17763.0` (Windows 10 1809 or newer), exposing the Windows notification APIs. Microsoft.Toolkit.Uwp.Notifications 7.1.3 provides the documented unpackaged WPF toast registration and COM activation path; no Windows App SDK bootstrapper or MSIX conversion is needed. The installer minimum version matches this requirement and calls the toolkit cleanup entry point on uninstall.

References: [Microsoft local toast guidance](https://learn.microsoft.com/zh-tw/windows/apps/develop/notifications/app-notifications/send-local-toast), [WebView2 notification lifecycle](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2notification?view=webview2-dotnet-1.0.4022.49).

Original failures: a four-second process-age notification blackout; two-string payloads discarded metadata; arbitrary live-region alerts were treated as notifications; title/label polling was localized, excessively broad and partly Direct-only; no central duplicate detection; only tray balloons; low-memory mode was applied even when notifications were enabled.

Sources now enter `NotificationService` as `InstagramNotification` records:

1. **Native WebView2** is preferred. The handler always marks the event handled, captures title, body, icon, body image, tag, origin, SDK timestamp and silent flag, then submits it without a startup blackout. Registration and permission errors are isolated from WebView initialization. Native content is retained, including Instagram's own group/media wording.
2. **Page Notification** is a transparent constructor proxy. It preserves the native constructor, prototype and bound static permission method. It forwards bounded structured payloads only when enabled and permission is granted. It does not intercept or modify service workers.
3. **Direct DOM** observes already-loaded thread links and explicit unread state/counts. A two-line, single-avatar layout or explicit conversation/preview semantics can provide real text. Ambiguous layouts use generic text. This primary-page fallback makes no additional inbox navigation or private API requests.
4. **Global unread badge** watches numeric counts inside the inbox navigation link on any Instagram route. It supplies generic wording when no detailed row transition is available.
5. **Background Direct inbox** is an optional enrichment source added incrementally on 2026-09-14; see the patch notes below. It uses the same coordinator, deduplicator, image cache and Windows presenter.

The bridge is an embedded asset independent of optional UI customization. Installer-preserved editable assets cannot leave an obsolete bridge installed. The host accepts only a small JSON object contract (16 KiB maximum, bounded depth/fields, exact expected Instagram HTTPS origins). Invalid types, sources, unread flags and Direct URLs are rejected.

## DOM initialization and false-positive controls

A document-scoped MutationObserver survives body/root replacement. It schedules at most one scan per 350 ms while mutations continue; scans target inbox/thread links instead of every element's labels/classes. A single 15-second safety timer covers missed SPA updates. Host SourceChanged and popstate also schedule scans.

Initial rows/counts are silent baselines. The observed notification state must settle for 650 ms before DOM transitions are armed, accommodating staged page hydration; this is not a process-age filter on genuine native/page notifications. SPA route changes and re-enabling detection establish fresh baselines. Newly discovered/virtualized rows and reappearing badges also baseline silently.

A row requires an unread transition, increasing explicit unread count, or a changed preview corroborated by an increasing inbox count. Preview edits alone, rerenders, outgoing changes without incoming evidence, document titles and arbitrary live regions are not notification sources. No hashed classes are used. English/Chinese/Japanese unread accessibility labels are supplementary hints only. Names are not fabricated and media type is not inferred from ambiguous DOM text.

## Arbitration, deduplication and lifetime

Detailed native candidates can be presented immediately. Page/DOM/badge candidates normally wait 750/1200/1800 ms to allow richer sources to join the same delivery. The inbox enrichment option holds eligible generic native/page candidates and Direct evidence for a two-second matching window. Matching uses strong IDs when supplied, same-source state sequences, or native tag plus real timestamp and content. A tag by itself is not a message ID: Instagram can reuse conversation tags.

Cross-source matching uses normalized text and compatible thread URLs within three seconds. A badge with no identity uses a narrower two-second correlation with native/page activity as a last resort. Each delivery consumes at most one distinct event from each source, so a second genuine same-source event or changed sequence is retained even when its text is identical. Late richer metadata silently replaces the existing toast using the same token rather than creating another popup.

Dedup state is capped at 256 records with 30-second expiration. At most 64 delivery/lifecycle records are held; notifications expire after one day. No dedup/DM content database is written.

## Windows presentation and activation

Toast text is escaped by ToastContentBuilder. The image loader runs outside the UI thread, uses HTTPS CDN allowlists without cookies or redirects, enforces 1 MiB downloads and bounded image dimensions, validates/encodes PNG, and applies a 1.5-second deadline. At most 48 image files are retained, with a one-day age limit cleaned on subsequent image work. Invalid/missing/expired/slow images return no image; text still proceeds. OS policy disables are respected instead of bypassed with a balloon. Initialization/show failures are logged without crashing browsing.

Successful `Show` submission is logged as **Windows accepted the request**, not proof that a banner appeared. Focus Assist/Do Not Disturb can hide banners. There is no Windows "banner visibly rendered" callback.

The toolkit activation handler dispatches to WPF. Activations during startup are queued. Existing single-instance mutex/event behavior remains, and the toolkit routes running-process COM activation to the registered callback. Closed-app toast activation can launch the executable; its token and validated Direct URL allow navigation without persisting message text. MainWindow restores from tray/minimized state and activates/focuses the existing WebView. A Direct URL arriving during initialization is queued so home navigation cannot overwrite it.

Only exact HTTPS `instagram.com` / `www.instagram.com` URLs with `/direct/t/<digits>/` paths are accepted; expected relative paths are normalized. Userinfo, nondefault ports, query/fragment data, escapes, dot segments, external origins, `file:` and `javascript:` are rejected. If no thread is known, activation restores the window. This SDK does not expose arbitrary notification `data`/click URLs; a native tag is used as a route only if it is itself a valid Direct URL, or matched DOM/page metadata supplies one.

For native events, the coordinator calls `ReportShown` after successful submission, `ReportClicked` on activation and `ReportClosed` on user dismissal/removal. Clicking is terminal: it is not followed by a second `ReportClosed` call, which the runtime rejects. Web `CloseRequested` removes the corresponding toast without echoing `ReportClosed`. Banner timeout retains the lifecycle because Notification Center can still be clicked. All COM reporting runs on WPF's dispatcher, handlers are detached on recovery/shutdown, and pending image work is cancelled.

## Settings, background and privacy

Desktop notifications preserve the existing AppNotifications setting. Disabled mode denies notification permission on the two expected origins, stops DOM scanning, ignores candidates and cancels/removes in-process deliveries. Enabling establishes a new DOM baseline. Historical notifications from an earlier process may remain in Windows Notification Center until dismissed or expired.

When notifications are enabled, minimized/tray WebViews use Normal memory and are never explicitly suspended. When notifications are disabled, the existing BackgroundLowMemory preference still selects Low memory. Normal browsing, user data paths, persisted sessions, tray behavior and updater remain; the updater is skipped in diagnostic modes so tests cannot initiate an update.

Logs include fixed layer events, exception type/HResult, source, body length and sender/avatar/thread presence bits. No names, message bodies, tags, URLs, cookies or tokens are logged. Candidate numeric code is `source*100000 + bodyLength*10 + senderPresent + avatarPresent*2 + threadPresent*4`. Diagnostic code is `kind*10000 + count` (1 baseline, 2 permission, 3 worker registrations, 4 active workers, 5 worker-query unavailable, 6 wrapper unavailable). Worker queries are read-only and do not implement web push.

## Repeatable verification

Verified on 2026-09-13 with SDK 8.0.301, self-contained runtime .NET 8.0.29 and WebView2 runtime 153.0.4234.32:

| Check | Result |
| --- | --- |
| `build-release.bat --no-pause` (restore, Release build, Stable publish) | Passed; 0 warnings, 0 errors |
| `build-single-file.bat --no-pause` | Passed |
| `scripts/verify-notifications.ps1` against Stable publish | 78 checks passed |
| Same runner against SingleFile publish | 78 checks passed |
| `node --check Assets/Scripts/notifications.js` | Passed |
| `git diff --check` | Passed |
| Existing `scripts/verify.ps1` | Starts, restores settings, loads signed-out Instagram; stops at the existing `CSS and JavaScript injection` assertion. MainWindow already forces `UiCustomization=false`, while this older test still expects injected customization. This unrelated assertion was not removed to mask the failure. |

Reports are written to `artifacts/verification/notifications.json`, `notifications-single-file.json`, and `legacy-smoke-notification-change.json`. Windows presentation is mocked in the notification suite, whereas DOM mutation/permission/memory checks and native WebView shown/clicked/dismissed/web-close callbacks execute against the actual WebView2 runtime. Real Windows banner visibility, COM cold activation and signed-in Instagram receipt remain manual tests.

## Changed-file inventory

Existing files:

- `App.xaml.cs`: toast startup activation, uninstall cleanup and isolated diagnostic entry point.
- `MainWindow.xaml.cs`: coordinator ownership, restore/navigation callbacks, removal of primary balloon presentation.
- `Services/WebViewService.cs`: native metadata/lifecycle routing, structured bridge, permissions, safe navigation and conditional memory policy.
- `Services/LoggingService.cs`: private-content-free notification layer diagnostics.
- `SettingsWindow.xaml`: explain the low-memory/notification relationship.
- `InstaDesktop.csproj`, `packages.lock.json`: compatible Windows target and toast package.
- `installer/InstaDesktop.iss`: Windows minimum and notification registration cleanup.
- `Diagnostics/SmokeTestRunner.cs`: update its memory expectation to follow the notification setting.
- `README.md`: minimum OS and documentation link.

New files:

- `Models/InstagramNotification.cs`: structured metadata and source/type enums.
- `Services/NotificationPolicy.cs`: bounded payload validation, normalization and URI policies.
- `Services/NotificationDeduplicator.cs`: bounded central correlation cache.
- `Services/NotificationService.cs`: arbitration, delivery state and native lifecycle ownership.
- `Services/WindowsNotificationService.cs`: unpackaged Windows toast display and registration.
- `Services/NotificationImageCache.cs`: asynchronous validated image caching.
- `Assets/Scripts/notifications.js`: page constructor fallback and DOM observer.
- `Diagnostics/NotificationTestRunner.cs`, `scripts/verify-notifications.ps1`: offline verification.
- `NOTIFICATIONS.md`: architecture, evidence, limitations and manual checks.

## Running the checks

```powershell
.\build-release.bat --no-pause
.\scripts\verify-notifications.ps1
node --check Assets/Scripts/notifications.js
```

The notification runner uses a fresh profile below the report directory, intercepts WebView web requests with local fixtures, and uses a fake Windows presenter. It checks URL/JSON validation, Unicode, source arbitration, duplicate/identical-message behavior, activation serialization, optional image failures, initial/staged hydration, SPA/global badge changes, body replacement, disabling/re-enabling, actual WebView native notification lifecycle callbacks, and background/permission behavior. It does not sign in, use your profile, register real toasts or contact another Instagram account.

## Remaining platform/UI limitations

- Instagram does not expose a stable supported DM DOM contract. Real current account layouts have not been inspected in these offline fixtures. If links, numeric badges or explicit unread semantics are absent, the bridge deliberately stays silent. The synthetic data attributes are optional adapters, not a claim that every Instagram deployment exposes them.
- Home/Reels can only reveal sender/preview if Instagram has placed that information in the loaded DOM or a native/page notification. Otherwise only the inbox badge can yield a generic message. Repeated messages in an already-unread conversation may leave all visible counts/previews unchanged, and capped badges such as `9+` may not change; those are not detectable without native payloads. DOM initialization also cannot distinguish arbitrarily delayed old state from new activity with perfect certainty.
- Several messages or unrelated Instagram activity arriving together can make anonymous badge correlation ambiguous; strict exactly-once delivery cannot be guaranteed without a shared message ID. Three-second correlation intentionally expires so genuine repeated text is not permanently suppressed.
- WebView2/Chromium may throttle background scheduling; keeping Normal memory and avoiding suspension cannot guarantee Instagram realtime updates, service-worker push delivery, or recovery from network/sleep/account restrictions. No new notifications are generated while the app is fully exited.
- Windows may deny registration, block notifications, suppress banners, or restrict foreground activation. Actual Windows banners, COM cold-launch activation and real account behavior require the manual tests below.

## Two-account manual test checklist

Use account A in InstaDesktop and account B on a phone or separate browser. Enable Desktop notifications in InstaDesktop and Windows notification settings; turn off Do Not Disturb for the banner tests. Keep a copy of the metadata-only app.log for troubleshooting.

1. With A's app exited, B sends unread messages into three existing conversations (use additional participants/group chats as needed). Start A and wait for the inbox badge to load: old unread state must not produce a notification storm.
2. Leave A on Home. B sends `hello`. Expect one notification, with B's actual name/message when a native payload exists, otherwise a truthful generic fallback. Repeat after moving A to Reels.
3. Repeat with A minimized, then minimized to the tray, including after several minutes. Restore A and confirm normal browsing/session persistence.
4. In a known Direct thread, B sends a new message. Expect one popup even if the native event and DOM transition both occur. Check the log for candidates and a duplicate decision rather than two popup submissions.
5. B sends `ok`, waits five seconds, then sends `ok` again. Expect two messages when native payloads or observable counter changes are supplied. Also try two rapid identical messages and two different conversations.
6. Have a group participant send text, a photo, and a voice message. Check that native wording/group names are retained; unknown types must not be guessed. Send a message as A: it must not itself generate an incoming-DM popup.
7. Click a notification with a known thread while A is in the tray. Expect restore/foreground activation and that exact thread. For a native payload with no safe route, expect restore and Instagram's own click behavior where available. Also test a retained notification after Tray > Exit to check cold launch and then repeat while another normal app launch occurs.
8. Dismiss a notification; separately let a banner time out and click it later in Notification Center. Confirm no lifecycle error in app.log. Native web-initiated closes should remove their corresponding item.
9. To exercise avatar failure with a real notification, briefly block only its image CDN in a local network/debugging tool while leaving Instagram messaging reachable, then have B send text. Expect text despite the missing image. Automated tests already cover HTTP/decode/oversize/timeout failures without changing your network.
10. Disable Desktop notifications, then B sends messages on Home/Reels/tray. Expect no new notification. Re-enable: existing unread state must baseline silently; B's next observable incoming transition should notify. Verify the low-memory preference still works only with notifications disabled.
11. Restart the app with unread messages again. Expect a fresh silent baseline and retained login/session. Repeat a Home/Direct/Reels navigation cycle without new messages: no notification should be caused solely by navigation.

These account tests are manual; a passing offline report is not evidence that they were performed.

## Incremental Direct inbox enrichment — 2026-09-14

The clean starting tree was commit `1f7bfa1`. This patch adds an optional observation source to that implementation. It does not replace Windows toast presentation, activation/deep links, image downloading/caching, the main-page bridge, app settings, startup or the WebView profile.

### Monitor and session lifetime

`DirectInboxMonitor` creates one CoreWebView2Controller using the primary CoreWebView2.Environment and its exact ProfileName/InPrivate setting. This is WebView2 profile sharing, not cookie copying or an external HTTP login. It uses a 1000×800 layout viewport with IsVisible=false and IsMuted=true. It is not added as a visible WPF control and never requests focus.

WebViewService starts it only with notifications enabled and a usable primary page containing the inbox navigation link, without a password form. SPA/full navigation and native notification receipt can trigger a readiness check; a 30-second check covers late login hydration. A failed/blocked creation has a two-minute retry backoff. Settings disable, primary session/challenge routes, controller replacement and shutdown dispose it. A controller whose asynchronous creation completes after disposal is immediately closed.

Its only navigation target is `https://www.instagram.com/direct/inbox/`. Full navigations away from that URL are cancelled; secondary-frame navigation, new windows, downloads and dialogs are suppressed. Native notifications are marked handled and forwarded to the shared coordinator, preserving their lifecycle; dropping them can lose the only notification received by the hidden controller. The monitor-specific script guards history.pushState/replaceState and stops on a disallowed SPA route. A navigation/session/renderer failure discards monitor evidence while preserving pending generic notifications. Denied monitor permissions are not saved into the shared profile.

No code clicks, scrolls, focuses or opens conversation rows, reads cookies/tokens, intercepts DM network payloads, calls private Instagram endpoints, or sends messages. The user must still verify Instagram's actual server-side read-receipt behavior with two accounts; fixture navigation checks cannot establish that behavior.

### Snapshot evidence and matching

The monitor has its own small embedded script, not the primary Notification constructor wrapper. A document-scoped MutationObserver batches changes at 500 ms with a 15-second safety check. It emits one changed snapshot containing at most 40 naturally loaded thread links. C# validates the inbox origin, document UUID, increasing sequence, JSON depth/size (256 KiB), field types/lengths, unique thread URLs, unread flags/count consistency and the existing Direct URL policy. Avatar URLs use the existing image policy/cache.

Extraction uses thread anchors, explicit unread state/counts, explicit conversation/preview/sender semantics, or a validated two-line, single-avatar row. It has no hashed-class selectors or localized "Unread"/"You:" matching. Explicit outgoing direction excludes a row. **These are defensive supported shapes, not evidence that every current Instagram deployment exposes those attributes.** Unsupported markup yields the existing generic fallback.

The first valid snapshot is silent. Reload/session reset creates a fresh silent baseline. Newly discovered or virtualized rows baseline silently even at the top; they are never assigned to a generic event merely because they appeared recently. An existing row is strong evidence when its preview changes with read→unread, or its unread count increases. A preview change while unread is weak evidence and needs native confirmation. An unclassified competing preview change vetoes attribution.

Eligible generic notifications require the expected Instagram origin, brand-only title and no sender, route, avatar or body-image metadata. A small multilingual allowlist of known generic wording (or an empty/default body) prevents replacing potentially meaningful branded activity alerts. Unknown wording and already-detailed native payloads retain their existing timing/content. This intentionally prefers missed enrichment over a false sender attribution.

The existing coordinator waits **2,000 ms** asynchronously for eligible generic candidates. This accommodates the 500 ms snapshot debounce and background scheduling without introducing a startup blackout. Inbox changes can arrive before or after the native event. The coordinator decides after the complete window, requiring exactly one candidate and exactly one eligible native/page delivery in that interval. Multiple competing changes/events or uncertain evidence preserve the original generic text; independently strong thread events may still display separately.

Successful enrichment updates the original delivery's title, preview, sender, avatar and safe thread URL while retaining native tag/timestamp/silent settings and lifecycle ownership. The existing dedup cache reassigns source records to that same delivery token; no second dedup cache or toast pipeline exists. The main WebView's Direct evidence can participate too, including when the monitor fails. A recognized generic badge/page/native combination cannot claim an arbitrary thread before this decision. Weak unconfirmed changes never display on their own. Distinct sequences and repeated messages several seconds apart retain the existing dedup behavior.

The displayed body is only the available inbox preview, including its ellipsis. No thread is opened to retrieve a complete message. Missing preview text keeps the original generic wording; an optional known name/route can still be retained. When several changes are ambiguous, additional verified DOM notifications can coexist with a generic alert; strict exactly-once association is impossible without a shared message identifier.

### Files in this incremental patch

- Added `Services/DirectInboxMonitor.cs`: owns the hidden shared-profile controller and bounded snapshot baseline.
- Added `Assets/Scripts/direct-inbox-monitor.js`: inbox-only observer and navigation guards.
- Extended `Services/WebViewService.cs`: monitor readiness, ownership, retry and disposal hooks.
- Extended `Services/NotificationService.cs`: two-second opportunity and unambiguous merge inside the existing delivery flow.
- Extended `Services/NotificationDeduplicator.cs`: defer anonymous attribution and merge delivery tokens in the existing cache.
- Extended `Models/InstagramNotification.cs`: DirectInbox source and native-confirmation flag; no duplicate notification model.
- Extended `Services/NotificationPolicy.cs`: conservative multilingual generic recognition; existing route/image validators retained.
- Extended `Services/LoggingService.cs`: fixed enrichment events only, preserving content-free logging.
- Extended `Diagnostics/NotificationTestRunner.cs`: snapshot, ambiguity, timing, fallback, shared-profile and hidden-controller fixtures.
- Updated this document. No package, settings UI, Windows presenter, image-cache, startup, activation or main notification script replacement.

### Manual two-account enrichment checks

Use A in InstaDesktop and B on a phone/separate browser, with Windows and app notifications enabled and Do Not Disturb off. First allow the inbox monitor to establish its baseline (metadata log: DirectMonitorReady / DirectMonitorBaselineCreated). Avoid opening the target thread in A until the explicit click test.

1. **A/F — Baseline and restart:** leave unread messages in existing conversations, start A, then restart again. Expect no stale-notification storm.
2. **B — Home:** keep A on Home. B sends `hello` in an existing conversation. Expect one enriched notification if supported inbox evidence is available, otherwise the original generic alert after approximately two seconds.
3. **C — Reels:** keep A on Reels and send another short message. Verify the main page stays on Reels and the notification uses the observed name/preview when available.
4. **D — Tray:** minimize A to tray for several minutes, then send again. Check both delivery and metadata logs; background scheduling/Instagram updates still require live validation.
5. **E — Long message:** send a long message. Compare the toast with the naturally visible inbox preview later. Ellipsis/truncation is acceptable; the monitor must not open the thread to expand it.
6. **G — Repeated text:** send `ok`, wait five seconds, then send `ok` again. Distinct native events or changing inbox counters must not be permanently collapsed by identical text.
7. **H — Monitor failure:** the offline suite exercises this automatically. For a live debug test, break on the UI thread inside WebViewService after the monitor has started, evaluate `_directMonitor.Dispose()` and `_nextDirectMonitorAttempt = DateTimeOffset.UtcNow.AddMinutes(5)` in the debugger, then continue and send from B. The primary native generic alert must still appear. Restart A afterwards to restore monitoring immediately.
8. **I — No read receipt (critical):** send from B while A remains on Home/Reels/tray. Do not click the toast or manually open the thread. Confirm on B that enhanced notification receipt alone did not mark the message Seen. This server-side behavior has not been verified by automated tests.
9. **J — Explicit click:** now click an enriched toast with a known route. A should restore and open the correct conversation through the unchanged activation code. Only this user action authorizes thread navigation.
10. **Ambiguity/outgoing/settings:** arrange near-simultaneous incoming messages in two conversations; no generic alert may be randomly attributed to one. Send as A and confirm no incoming toast solely from an outgoing preview change. Disable notifications and confirm the monitor stops; re-enable and confirm a fresh silent baseline.

If Instagram omits thread anchors, explicit unread state, or a reliable preview, enrichment remains unavailable. Newly appearing virtualized rows, saturated counters, unknown generic wording, delayed background rendering and missing native events can also limit detail/association. The generic path, previous settings and existing notification implementation remain available.

### Incremental patch verification

- `build-release.bat --no-pause`: restore, Release build and Stable publish passed with zero warnings/errors.
- `dotnet publish InstaDesktop.csproj -c Release -p:PublishProfile=SingleFile`: passed without warnings/errors.
- `node --check Assets/Scripts/direct-inbox-monitor.js`: passed.
- `scripts/verify-notifications.ps1 -Report artifacts/verification/inbox-enrichment-release.json`: all 119 checks passed, including the original 78 notification checks.
- `scripts/verify-notifications.ps1 -Exe publish/single-file/InstaDesktop.exe -Report artifacts/verification/inbox-enrichment-single-file.json`: all 119 checks passed from the single-file bundle too.
- Coverage includes generic-first/inbox-first matching, rich native bypass of a pending inbox wait without duplicate submission, ambiguity, weak evidence, failure fallback, shared profile storage, hidden/muted controller behavior, monitor native suppression, DOM replacement, blocked SPA thread navigation and disposal during creation.
- Windows presentation uses the existing fake presenter. The controller, native lifecycle and DOM checks use the actual WebView2 runtime with intercepted local fixtures in an isolated diagnostic profile. No signed-in Instagram session, real Windows banner or two-account read receipt was tested.
- Final diff review found no dependency, settings UI, primary bridge, Windows presenter, image-cache or activation replacement. Restore-only lockfile changes were not retained.

## Notification recovery repair (2026-09-14, evening)

The running executable was the installed September 11 build, while the September 14 publish contained the newer notification implementation. The latest installed-process log entries showed only `AppStarted`, without notification registration or permission initialization, even though `AppNotifications` was enabled. The existing Start Menu shortcut also targeted a nonexistent sandbox-user installation. Building a publish does not update either the actual installation or its shortcuts.

Four code paths were repaired:

- Hidden inbox native notifications now enter the same validated payload mapper, coordinator and native lifecycle handling as the primary WebView.
- Discarding pending inbox evidence also forgets its deduplication records. A following matching native/page notification can no longer resolve to an abandoned delivery and disappear.
- A matching page notification or strong primary DOM event can restart delivery after the original weak inbox evidence has finished waiting without confirmation.
- Resetting website permissions reapplies the desktop notification preference to both supported Instagram origins.

Validation for this repair:

- The new core regression reproduced `monitor reset cannot suppress matching native notification` before the fix (`artifacts/verification/notification-repair-core-before.json`).
- Stable and SingleFile self-contained publishing succeeded. Their core suites each passed 85 checks (`notification-repair-release-core.json`, `notification-repair-single-file-core.json`). These cover policy, deduplication, arbitration, image failure, inbox state and recovery. They do not exercise WebView2 or actual Windows presentation.
- Both JavaScript bridges passed syntax checks. The repair script passed parser, isolated copy/preservation and rollback checks, plus read-only planning against the actual installation.
- Full WebView integration could not complete in the sandbox: WebView2 subprocess crashes were logged and the runner exceeded 60 seconds. Automatic review of a normal-Windows retry returned a service-unavailable error. The added hidden-controller lifecycle and permission-reset tests therefore remain pending; real Windows banners and signed-in receipt also remain unverified.

`scripts/verify-notifications.ps1 -CoreOnly` explicitly runs the core subset and records `scope: notification-core`. The default still runs the full integration suite, including the new tests. Diagnostic runs use separate profiles and simulated Windows presentation.

After building and verifying the publish, `scripts/repair-local-install.ps1` can update an existing installation using explicit absolute `-InstallDirectory` and `-ShortcutPath` values. `-WhatIf` previews changes. It backs up changed files and the existing shortcut under `artifacts/install-backups`, requests orderly exit of the exact installed process, preserves existing custom Assets and profile/settings data, verifies installed hashes, restores files on copy failure, and restarts the installed executable. Deployment is a separate action; a successful publish or core test alone does not repair an already-installed copy.

The attempt to apply this repair to the actual installation was also blocked by automatic approval review returning HTTP 503. No installed files, shortcuts or running user process were changed by that attempt. The verified publish and repair script are ready; actual deployment and full integration verification still require an allowed execution context.

## Sender and message preview repair (2026-09-14)

The later report came from the current publish, not the old installed copy. The live log identified a badge-only candidate (`300220`) and an empty inbox baseline (`DirectMonitorBaselineCreated Code=0`). The user confirmed that the inbox visibly displays conversation names and message previews. The previous extractor required thread anchors and unusually strict two-line markup; generic badge events also bypassed enrichment entirely.

The embedded `direct-inbox-dom.js` extractor is now shared by the primary notification bridge and hidden inbox. It accepts thread anchors and avatar/name/preview button rows on Direct routes, removes separate time/status tokens, handles group sender prefixes, recognizes explicit unread semantics and constrained unread-dot/contrasting typography layouts, and rejects compose controls, outgoing previews and ambiguous same-name button rows. A known row changing from unknown/read to unread with a changed preview is incoming evidence. Baselines and rerenders remain silent. Stable local conversation keys let rows without an href supply content without inventing a thread navigation URL.

Badge events now wait for one strong matching inbox delta. Late higher-priority generic payloads preserve already-extracted names and text. Badge/native receipt requests an immediate inbox DOM snapshot to reduce background timer delay. Preview-only edits still need independent native/page confirmation, and multiple competing changes do not arbitrarily choose a sender.

`DirectMonitorExtraction` logs only counts: `rowCount*1000000 + namedCount*10000 + previewCount*100 + unreadCount`. No extracted name, message or conversation key is logged.

Validation: 99 core notification checks passed. `node scripts/test-direct-inbox-dom.cjs --dom --notifications` passed 37 DOM fixtures, including primary-bridge payloads and hidden-monitor snapshots; this mode uses jsdom with explicit fixture geometry, not real browser rendering. JavaScript syntax checks passed. Full WebView integration again hit sandbox browser subprocess crashes. Automatic approval service HTTP 503 prevented reading the actual app UI and launching browser tests outside that restriction, so actual current-account DOM and real Windows banners remain unverified.

The new self-contained build is in `publish/sender-preview/InstaDesktop.exe`; the already-running `publish/win-x64` copy cannot pick up changes until exited. Exit through the tray menu before launching the new build: the single-instance guard otherwise reactivates the old process. Real-account verification should show a nonzero extraction row/name/preview count, then a detailed notification for a new incoming message. Layouts with no readable preview, ambiguous identity, or no observable incoming state still fall back to generic text. Button rows without a genuine href restore the window when clicked.

## Background notification recovery (2026-09-14, late evening)

The running `sender-preview` build read 14–16 conversation names and previews but reported zero recognized unread rows. Later logs showed badge notifications submitted to Windows while detail enrichment timed out. The user reported missed notifications on Home/Reels or in the tray. These logs do not establish the actual unread markup or prove every preview change is incoming.

The primary bridge now gives the global Messages badge an independent settled baseline, retains it across SPA routes and temporary missing navigation, and settles decreases to avoid replaying old counts during navigation rebuilding. Optional row extraction exceptions no longer abort badge delivery or prevent creation of the safety timer. Hidden-monitor extraction failures invalidate stale details and recover with a silent baseline.

For a continuously observed row whose unread state remains unknown, a real preview delta now retains its name/body as weak evidence. It stays silent on its own. Exactly one nearby badge increase can confirm it only after the full two-second coordinator window, with exactly one candidate and no competing changes. Explicitly read rows and known-unread edits still cannot use badge confirmation. Outgoing previews are ignored; transitions from outgoing previews require native/page confirmation. Appearing/disappearing conversations, or another row becoming unread with identical text, veto anonymous attribution. This remains temporal correlation rather than a server message identifier; simultaneous invisible changes, unrendered rows, identical repeated previews and saturated badges limit detail recovery.

Diagnostics remain free of private message data. `DirectMonitorUnreadState` encodes `unreadRows*10000 + readRows*100 + unknownRows`; `DirectMonitorUncertainChange` records ambiguity and `DirectMonitorExtractionFailed` records monitor extraction failure. Primary `rows-unavailable` maps to `NotificationDiagnostic Code=70001` once per continuous failure.

The regression first failed on unknown-unread preview preservation (`background-notifications-before.json`). Five badge regressions also failed before the JavaScript fix: initial/later extractor exceptions, continuous row churn, SPA changes and temporary missing navigation. The final core suite adds state, ambiguity, outgoing, recovery and both event orders. The asynchronous delivery test waits for actual presentation within a bounded deadline instead of depending on a fixed 250 ms dispatcher allowance. The DOM suite passes 49 checks using jsdom fixture geometry, including hidden-observer failure and recovery.

The new self-contained builds are `publish/background-notifications/InstaDesktop.exe` and `publish/background-notifications-single-file/InstaDesktop.exe`; each passed 124 core checks. Core reports are `artifacts/verification/background-notifications-release-core.json` and `background-notifications-single-file-core.json`. Publishing emits NU1900 because the restricted environment cannot reach NuGet's vulnerability service. Real signed-in Windows notification delivery has not been verified: desktop window discovery and the authorized launch of the new build were rejected by automatic approval review with HTTP 503. The old process exited independently before the launch attempt; automation did not start the new process.

## Activity tile extraction defect (2026-09-14, 23:35 report)

The current `background-notifications` executable still submitted generic badge notifications. Its only detailed candidates occurred about one minute apart with `Code=400142`: a 14-character preview, an avatar, and no sender field or thread URL. Badge events had no nearby detail and timed out. The candidate count therefore does not prove that the real DM conversation rows were read.

A DOM regression reproduced an exact matching shape: the avatar button `Alice / Active 10m ago` was accepted, and changing its online status to `Active 11m ago` produced a different 14-character preview. This is a confirmed extractor defect and a strong explanation for the observed cadence, but the actual account DOM has not been inspected. The first-pass inferred button extractor now excludes status-only text; explicit preview fields and canonical thread URLs still preserve messages whose literal contents use that wording. Six added regressions pass, bringing the DOM suite to 55. The first new regression failed before the fix. The targeted build is `publish/inbox-status-fix/InstaDesktop.exe`.

This correction removes false evidence; it does not establish that the current account's real conversation rows are supported. Browser inventory was also blocked by automatic approval review HTTP 503. A screenshot of the actual inbox conversation list is needed to identify the missing row structure before declaring sender/content recovery successful. No correlation window or incoming-evidence requirement was loosened in this patch.

## Hidden monitor permission and native lifetime repair (2026-09-29)

Causes. (1) `DirectInboxMonitor` answered every `PermissionRequested` with Deny, including Instagram Notifications while AppNotifications was on. Whenever the shared profile was not already Allow (never synchronized, reset, or synchronization failed), the background inbox could never obtain Notification permission, so the only page able to raise a native event while the primary is on another route produced none, and preview-only inbox evidence waiting for native confirmation was never shown. The integration test masked this by calling `SetPermissionStateAsync(...Allow)` itself. (2) Native `CoreWebView2Notification` objects from a monitor were kept after its controller closed (route block, renderer crash, restart). A later toast click, dismissal, one-day expiry, 64-entry eviction or primary reinitialization called `ReportClicked`/`ReportClosed` on a closed controller: an uncatchable `AccessViolationException` that terminated the app.

Fixes. `NotificationPermissionPolicy` is the single owner of the rules. The monitor allows Notifications only for the exact www/root Instagram origins while AppNotifications is on, saving the grant like the settings sync does; a denial is request-only so the background page never overwrites the profile, and every other permission/origin stays denied with no dialog. The settings sync targets exact origins, re-applies if the setting changes mid-sync, and logs the stored state (`NotificationPermissionState`, www + 10 × root). Monitor handlers are named and detached on dispose; native lifetimes record their owning controller and are released before that controller closes (and on primary renderer failure). Native events raised by both the primary page and the monitor for one message collapse into one delivery. The Windows presenter logs `CreateToastNotifier().Setting` on startup and every show, names each system disable reason, treats an unreadable setting as "attempt Show", and separates Show exceptions (`WindowsNotificationShowFailed`) from `ToastNotification.Failed` (`WindowsNotificationDeliveryFailed`). No tray balloon substitutes for a toast.

Tests (`Diagnostics/NotificationMonitorPermissionTests.cs`). No WebView check grants permission from the test: the monitor starts from an ungranted profile and must obtain Allow through its own handler; disabled mode must deny without persisting and show neither browser UI nor a toast. The real `WebViewService` path is exercised with the primary on Reels, primary/monitor mirrored events, concurrent refreshes, SPA routes, monitor route block and restart, monitor renderer crash, primary reinitialization, setting off/on, restart over a stale denied profile, and permission reset. The real toast presenter is exercised through a notifier adapter (enabled, each disable reason, unreadable setting, Show exception, Failed, timeout) without registering a toast.
