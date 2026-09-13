# Instagram notification implementation

## Architecture and compatibility

The application remains unpackaged, x64, .NET 8 WPF with WebView2 SDK 1.0.4191.47 and the existing Evergreen runtime/profile. The target is now `net8.0-windows10.0.17763.0` (Windows 10 1809 or newer), exposing the Windows notification APIs. Microsoft.Toolkit.Uwp.Notifications 7.1.3 provides the documented unpackaged WPF toast registration and COM activation path; no Windows App SDK bootstrapper or MSIX conversion is needed. The installer minimum version matches this requirement and calls the toolkit cleanup entry point on uninstall.

References: [Microsoft local toast guidance](https://learn.microsoft.com/zh-tw/windows/apps/develop/notifications/app-notifications/send-local-toast), [WebView2 notification lifecycle](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2notification?view=webview2-dotnet-1.0.4022.49).

Original failures: a four-second process-age notification blackout; two-string payloads discarded metadata; arbitrary live-region alerts were treated as notifications; title/label polling was localized, excessively broad and partly Direct-only; no central duplicate detection; only tray balloons; low-memory mode was applied even when notifications were enabled.

Sources now enter `NotificationService` as `InstagramNotification` records:

1. **Native WebView2** is preferred. The handler always marks the event handled, captures title, body, icon, body image, tag, origin, SDK timestamp and silent flag, then submits it without a startup blackout. Registration and permission errors are isolated from WebView initialization. Native content is retained, including Instagram's own group/media wording.
2. **Page Notification** is a transparent constructor proxy. It preserves the native constructor, prototype and bound static permission method. It forwards bounded structured payloads only when enabled and permission is granted. It does not intercept or modify service workers.
3. **Direct DOM** observes already-loaded thread links and explicit unread state/counts. A two-line, single-avatar layout or explicit conversation/preview semantics can provide real text. Ambiguous layouts use generic text. No requests are made to Instagram private endpoints and no hidden inbox is opened.
4. **Global unread badge** watches numeric counts inside the inbox navigation link on any Instagram route. It supplies generic wording when no detailed row transition is available.

The bridge is an embedded asset independent of optional UI customization. Installer-preserved editable assets cannot leave an obsolete bridge installed. The host accepts only a small JSON object contract (16 KiB maximum, bounded depth/fields, exact expected Instagram HTTPS origins). Invalid types, sources, unread flags and Direct URLs are rejected.

## DOM initialization and false-positive controls

A document-scoped MutationObserver survives body/root replacement. It schedules at most one scan per 350 ms while mutations continue; scans target inbox/thread links instead of every element's labels/classes. A single 15-second safety timer covers missed SPA updates. Host SourceChanged and popstate also schedule scans.

Initial rows/counts are silent baselines. The observed notification state must settle for 650 ms before DOM transitions are armed, accommodating staged page hydration; this is not a process-age filter on genuine native/page notifications. SPA route changes and re-enabling detection establish fresh baselines. Newly discovered/virtualized rows and reappearing badges also baseline silently.

A row requires an unread transition, increasing explicit unread count, or a changed preview corroborated by an increasing inbox count. Preview edits alone, rerenders, outgoing changes without incoming evidence, document titles and arbitrary live regions are not notification sources. No hashed classes are used. English/Chinese/Japanese unread accessibility labels are supplementary hints only. Names are not fabricated and media type is not inferred from ambiguous DOM text.

## Arbitration, deduplication and lifetime

Native candidates can be presented immediately. Page/DOM/badge candidates wait 750/1200/1800 ms to allow richer sources to join the same delivery. Matching uses strong IDs when supplied, same-source state sequences, or native tag plus real timestamp and content. A tag by itself is not a message ID: Instagram can reuse conversation tags.

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
