# InstaDesktop

<p align="center"><strong>A lightweight Instagram desktop window for Windows.</strong><br>Built with C#, .NET 8, WPF, and Microsoft Edge WebView2.</p>

<p align="center"><img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D4?logo=windows11&logoColor=white"> <img alt=".NET" src="https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white"> <img alt="License" src="https://img.shields.io/badge/license-MIT-green"></p>

## ✨ Features

- Native Windows experience for [Instagram](https://www.instagram.com/)
- WebView2-based; no Electron, Node.js runtime, or private Instagram API
- System tray, single-instance behavior, and persistent window layout
- Windows desktop DM notifications with native payloads, conservative DOM fallback, and safe thread activation
- Editable CSS and JavaScript assets
- Self-contained, single-file, and automatic GitHub Release updates

## 📦 Install

Download **`InstaDesktop-Setup.exe`** from [Releases](https://github.com/kurtxtb/InstaDesktop/releases), then run it. The per-user installer requires no administrator rights, preserves your profile and custom `Assets`, and upgrades in place.

> Microsoft Edge WebView2 Evergreen Runtime is required. Install it from the [official download page](https://developer.microsoft.com/microsoft-edge/webview2/) if needed.

## 🚀 Automatic updates

At startup, InstaDesktop checks the latest GitHub Release for `InstaDesktop-Setup.exe`. A newer version is downloaded, optionally verified with SHA-256, silently installed, and the app is restarted. The default repository is `kurtxtb/InstaDesktop`.

## 🛠️ Build from source

Requirements: Windows 10 1809 or newer / Windows 11 x64, [.NET SDK 8.0.300+](https://dotnet.microsoft.com/download/dotnet/8.0), and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
.\build-release.bat --no-pause
.\build-installer.bat --no-pause
```

| Artifact | Location |
| --- | --- |
| Stable app | `publish\\win-x64\\InstaDesktop.exe` |
| Installer | `publish\\installer\\InstaDesktop-Setup.exe` |
| Single-file app | `publish\\single-file\\InstaDesktop.exe` |

Building does not update an existing installation. To repair a local installation after verification, use `scripts/repair-local-install.ps1` with the actual absolute `-InstallDirectory` and existing `-ShortcutPath`; add `-WhatIf` to preview the update. It backs up replaced files and repairs the shortcut while retaining your profile, settings, and existing custom Assets.

## 🔐 Privacy

Notification architecture, platform limitations, automated checks and the two-account manual checklist are documented in [NOTIFICATIONS.md](NOTIFICATIONS.md).

InstaDesktop displays Instagram in WebView2. It does not read, store, or transmit your Instagram password; login data is managed by the WebView2 profile on your device.

### Calls: microphone and camera

Calls need three separate permissions: the **Allow microphone / Allow camera** switches in Settings > Calls (off by default), your answer when InstaDesktop first asks for `www.instagram.com` or `instagram.com`, and Windows' *Let desktop apps access your microphone / camera* (Settings has shortcuts). Only those two exact origins may ask.

- Switch off: requests are denied for that request only (nothing is saved), a tray notice links to Settings, and any saved answer is cleared, which also stops a microphone or camera already in use.
- Your Yes or No is remembered across restarts. To be asked again, turn the switch off and on (saving each time) or use **Reset website permissions**. The first start of this version clears a saved denial once, because older builds saved their own switch-off denials that could not be undone.
- Calls open in their own window (a real pop-up with `window.opener`), so the main window stays usable; ending the call closes only that window.
- The hidden background inbox page can never use the microphone or camera.

`scripts/verify-media-permissions.ps1` checks this with Chromium's virtual devices in an isolated profile; it does not place calls.

## 📁 Project layout

```text
Assets/                    Editable scripts, styles, and icons
Services/                  WebView, settings, logging, and update services
installer/InstaDesktop.iss Inno Setup definition
build-release.bat          Build and publish stable output
build-installer.bat        Create the Windows installer
```

## 📄 License

MIT License. See [LICENSE](LICENSE) when included in a distribution.
