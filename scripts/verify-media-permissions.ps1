param(
    [string]$Exe = 'publish\win-x64\InstaDesktop.exe',
    [string]$Report = 'artifacts\verification\media-permissions.json',
    [switch]$CoreOnly
)
# Isolated diagnostic profile, offline fixtures and Chromium virtual capture
# devices only. Never touches the signed-in profile, a real device or a call.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Exe))
$reportPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Report))
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build or publish the application first.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
$testArguments = '--media-permission-test "' + $reportPath + '"'
if ($CoreOnly) { $testArguments += ' --media-core-only' }
$process = Start-Process -FilePath $exePath -ArgumentList $testArguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(300000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw 'Media permission test exceeded its 300-second deadline.'
}
$process.Refresh()
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Media permission test wrote no report.' }
$result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $result.success) { throw ('Media permission test failed: ' + $result.failure) }
Write-Output ('Media permission checks passed: ' + @($result.checks.PSObject.Properties).Count)
Write-Output $reportPath
