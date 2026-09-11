# InstaDesktop

<p align="center"><strong>A lightweight Instagram desktop window for Windows.</strong><br>Built with C#, .NET 8, WPF, and Microsoft Edge WebView2.</p>

<p align="center"><img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D4?logo=windows11&logoColor=white"> <img alt=".NET" src="https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white"> <img alt="License" src="https://img.shields.io/badge/license-MIT-green"></p>

## ✨ Features

- Native Windows experience for [Instagram](https://www.instagram.com/)
- WebView2-based; no Electron, Node.js runtime, or private Instagram API
- System tray, single-instance behavior, and persistent window layout
- Editable CSS and JavaScript assets
- Self-contained, single-file, and automatic GitHub Release updates

## 📦 Install

Download **`InstaDesktop-Setup.exe`** from [Releases](https://github.com/kurtxtb/InstaDesktop/releases), then run it. The per-user installer requires no administrator rights, preserves your profile and custom `Assets`, and upgrades in place.

> Microsoft Edge WebView2 Evergreen Runtime is required. Install it from the [official download page](https://developer.microsoft.com/microsoft-edge/webview2/) if needed.

## 🚀 Automatic updates

At startup, InstaDesktop checks the latest GitHub Release for `InstaDesktop-Setup.exe`. A newer version is downloaded, optionally verified with SHA-256, silently installed, and the app is restarted. The default repository is `kurtxtb/InstaDesktop`.

## 🛠️ Build from source

Requirements: Windows 10/11 x64, [.NET SDK 8.0.300+](https://dotnet.microsoft.com/download/dotnet/8.0), and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
.\build-release.bat --no-pause
.\build-installer.bat --no-pause
```

| Artifact | Location |
| --- | --- |
| Stable app | `publish\\win-x64\\InstaDesktop.exe` |
| Installer | `publish\\installer\\InstaDesktop-Setup.exe` |
| Single-file app | `publish\\single-file\\InstaDesktop.exe` |

## 🔐 Privacy

InstaDesktop displays Instagram in WebView2. It does not read, store, or transmit your Instagram password; login data is managed by the WebView2 profile on your device.

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
