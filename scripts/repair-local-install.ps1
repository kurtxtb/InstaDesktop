<#
.SYNOPSIS
Updates an existing local installation from a verified, self-contained publish.
.DESCRIPTION
Pass the actual user's installation and existing Start Menu shortcut explicitly.
Does not read or change settings, WebView profiles, or notification preferences.
Existing Assets files are retained. Changed files and the shortcut are backed up
under artifacts/install-backups before deployment. Copy/verification failures
restore those backups and remove only individual files added by this operation.
Run the publish verification scripts first. -WhatIf performs read-only planning.
.EXAMPLE
.\scripts\repair-local-install.ps1 -InstallDirectory 'C:\Users\CodyTw\AppData\Local\Programs\InstaDesktop' -ShortcutPath 'C:\Users\CodyTw\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\InstaDesktop\InstaDesktop.lnk' -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [Parameter(Mandatory = $true)][string]$ShortcutPath,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\publish\win-x64'),
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert-NoReparsePoint([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a reparse point in path: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Get-ChildPath([string]$Root, [string]$Relative) {
    $child = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $child.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the intended directory: $child"
    }
    Assert-NoReparsePoint $child
    return $child
}

if (-not [IO.Path]::IsPathRooted($InstallDirectory) -or -not [IO.Path]::IsPathRooted($ShortcutPath)) {
    throw 'InstallDirectory and ShortcutPath must be explicit absolute paths for the actual user.'
}
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$sourceRoot = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\')
$shortcutFile = [IO.Path]::GetFullPath($ShortcutPath)
foreach ($path in @($installRoot, $sourceRoot, $shortcutFile)) { Assert-NoReparsePoint $path }
if ($installRoot -eq $sourceRoot -or
    $installRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $sourceRoot.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish and installation directories must be separate, non-overlapping directories.'
}
$installedExe = Get-ChildPath $installRoot 'InstaDesktop.exe'
if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) { throw "Existing installation not found: $installedExe" }
if ([IO.Path]::GetExtension($shortcutFile) -ne '.lnk' -or -not (Test-Path -LiteralPath $shortcutFile -PathType Leaf)) {
    throw "An existing Windows shortcut is required: $shortcutFile"
}
$requiredFiles = @(
    'InstaDesktop.exe', 'InstaDesktop.dll', 'InstaDesktop.deps.json', 'InstaDesktop.runtimeconfig.json',
    'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll',
    'Microsoft.Toolkit.Uwp.Notifications.dll', 'Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll',
    'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll'
)
foreach ($relative in $requiredFiles) {
    $source = Get-ChildPath $sourceRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
        throw "Incomplete self-contained publish: $source"
    }
}
if (@(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force | Where-Object {
    $_.Attributes -band [IO.FileAttributes]::ReparsePoint
}).Count) { throw 'The publish directory must not contain reparse points.' }

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutFile)
$shortcutBefore = [ordered]@{ targetPath = $shortcut.TargetPath; workingDirectory = $shortcut.WorkingDirectory }
$shortcutNeedsRepair = $shortcut.TargetPath -ne $installedExe -or $shortcut.WorkingDirectory -ne $installRoot
$files = @()
$preservedAssets = 0
foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force) {
    $relative = $file.FullName.Substring($sourceRoot.Length + 1)
    $destination = Get-ChildPath $installRoot $relative
    if (Test-Path -LiteralPath $destination -PathType Container) { throw "File destination is a directory: $destination" }
    $exists = Test-Path -LiteralPath $destination -PathType Leaf
    if ($exists -and $relative.StartsWith('Assets\', [StringComparison]::OrdinalIgnoreCase)) {
        $preservedAssets++
        continue
    }
    $newHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $oldHash = if ($exists) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash } else { $null }
    if ($newHash -eq $oldHash) { continue }
    $files += [pscustomobject]@{
        relativePath = $relative; source = $file.FullName; destination = $destination
        existed = $exists; previousSha256 = $oldHash; publishedSha256 = $newHash; backup = $null
    }
}
$running = @(Get-Process -Name InstaDesktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe })
Write-Output ("Plan: {0} changed/new files; {1} existing Assets files preserved; shortcut repair: {2}; matching running processes: {3}." -f
    $files.Count, $preservedAssets, $shortcutNeedsRepair, $running.Count)
if (-not $PSCmdlet.ShouldProcess($installRoot, "Repair from $sourceRoot and update existing shortcut $shortcutFile")) { return }

$backupRoot = Join-Path $projectRoot ('artifacts\install-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Assert-NoReparsePoint $backupRoot
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$manifestPath = Join-Path $backupRoot 'manifest.json'
$manifest = [ordered]@{
    startedAt = [DateTimeOffset]::Now.ToString('o'); status = 'backing-up'; publishDirectory = $sourceRoot
    installDirectory = $installRoot; shortcutPath = $shortcutFile; shortcutBefore = $shortcutBefore
    shortcutChanged = $shortcutNeedsRepair; shortcutBackup = $null; files = $files
    preservedAssets = $preservedAssets; stoppedProcessIds = @(); restartedProcessId = $null
    requiredFiles = $requiredFiles; verifiedHashes = @(); failure = $null; rollbackErrors = @()
}
function Save-Manifest { $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8 }
Save-Manifest
foreach ($entry in $files) {
    if ($entry.existed) {
        $entry.backup = Get-ChildPath $backupRoot ('files\' + $entry.relativePath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($entry.backup)) | Out-Null
        Copy-Item -LiteralPath $entry.destination -Destination $entry.backup
        if ((Get-FileHash -LiteralPath $entry.backup -Algorithm SHA256).Hash -ne $entry.previousSha256) {
            throw "Backup verification failed; installation was not changed: $($entry.destination)"
        }
    }
}
if ($shortcutNeedsRepair) {
    $manifest.shortcutBackup = Join-Path $backupRoot 'original-shortcut.lnk'
    Copy-Item -LiteralPath $shortcutFile -Destination $manifest.shortcutBackup
}
$manifest.status = 'backed-up'
Save-Manifest

# Closing the main window can minimize InstaDesktop to the tray. Instead request
# the app's normal session-ending cleanup on windows owned by this exact process.
# Never broadcast these messages or force-kill a process that refuses to exit.
if ($running.Count -gt 0 -and -not ('InstaDesktopRepair.NativeExit' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace InstaDesktopRepair {
    public static class NativeExit {
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(
            IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        public static void RequestExit(int processId) {
            var windows = new List<IntPtr>();
            EnumWindows((hwnd, param) => { uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid == processId) windows.Add(hwnd); return true; }, IntPtr.Zero);
            foreach (var hwnd in windows) {
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid != processId) continue;
                IntPtr result;
                // ENDSESSION_CLOSEAPP; SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT.
                var sent = SendMessageTimeout(hwnd, 0x0011, IntPtr.Zero, new IntPtr(1), 0x22, 3000, out result);
                if (sent == IntPtr.Zero || result == IntPtr.Zero) continue;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == processId) SendMessageTimeout(hwnd, 0x0016, new IntPtr(1), new IntPtr(1), 0x22, 3000, out result);
            }
        }
    }
}
'@
}
$touched = @()
$shortcutTouched = $false
try {
    foreach ($process in $running) {
        if ($process.HasExited) { continue }
        if ($process.Path -ne $installedExe) { throw 'The matching process changed before shutdown.' }
        [InstaDesktopRepair.NativeExit]::RequestExit($process.Id)
        if (-not $process.WaitForExit(15000)) { throw 'InstaDesktop did not exit cleanly. Exit it from its tray menu and rerun the repair.' }
        $manifest.stoppedProcessIds += $process.Id
    }
    $manifest.status = 'deploying'
    Save-Manifest
    foreach ($entry in $files) {
        Assert-NoReparsePoint $entry.destination
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($entry.destination)) | Out-Null
        $touched += $entry
        Copy-Item -LiteralPath $entry.source -Destination $entry.destination -Force
        if ((Get-FileHash -LiteralPath $entry.destination -Algorithm SHA256).Hash -ne $entry.publishedSha256) {
            throw "Installed file does not match the planned publish: $($entry.destination)"
        }
    }
    if ($shortcutNeedsRepair) {
        Assert-NoReparsePoint $shortcutFile
        $shortcutTouched = $true
        $shortcut.TargetPath = $installedExe
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.Save()
        $checkedShortcut = $shell.CreateShortcut($shortcutFile)
        if ($checkedShortcut.TargetPath -ne $installedExe -or $checkedShortcut.WorkingDirectory -ne $installRoot) {
            throw 'The repaired shortcut failed verification.'
        }
    }
    foreach ($relative in $requiredFiles) {
        $installed = Get-ChildPath $installRoot $relative
        $source = Get-ChildPath $sourceRoot $relative
        $hash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
        if ($hash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) { throw "Required dependency differs: $relative" }
        $manifest.verifiedHashes += [ordered]@{ relativePath = $relative; sha256 = $hash }
    }
    $manifest.status = 'verified'
    Save-Manifest
}
catch {
    $failure = $_
    $manifest.failure = $failure.Exception.Message
    if ($shortcutTouched) {
        try {
            Assert-NoReparsePoint $shortcutFile
            Copy-Item -LiteralPath $manifest.shortcutBackup -Destination $shortcutFile -Force
        } catch { $manifest.rollbackErrors += $_.Exception.Message }
    }
    [Array]::Reverse($touched)
    foreach ($entry in $touched) {
        try {
            Assert-NoReparsePoint $entry.destination
            if ($entry.existed) {
                Copy-Item -LiteralPath $entry.backup -Destination $entry.destination -Force
                if ((Get-FileHash -LiteralPath $entry.destination -Algorithm SHA256).Hash -ne $entry.previousSha256) {
                    throw "Restored file failed verification: $($entry.destination)"
                }
            } elseif (Test-Path -LiteralPath $entry.destination -PathType Leaf) {
                Remove-Item -LiteralPath $entry.destination -Force
            }
        } catch { $manifest.rollbackErrors += $_.Exception.Message }
    }
    $manifest.status = if ($manifest.rollbackErrors.Count) { 'rollback-incomplete' } else { 'rolled-back' }
    Save-Manifest
    throw "Repair failed: $($failure.Exception.Message) Review $manifestPath. The application was left stopped."
}

if (-not $NoRestart) {
    try {
        $launched = Start-Process -FilePath $installedExe -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
        $manifest.restartedProcessId = $launched.Id
    } catch {
        $manifest.status = 'verified-restart-failed'
        $manifest.failure = $_.Exception.Message
        Save-Manifest
        throw "Files and shortcut are repaired, but restart failed. Review $manifestPath."
    }
}
$manifest.status = 'complete'
$manifest.completedAt = [DateTimeOffset]::Now.ToString('o')
Save-Manifest
Write-Output "Repair verified. Backup and manifest: $manifestPath"
