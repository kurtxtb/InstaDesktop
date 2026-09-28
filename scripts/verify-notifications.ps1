param(
    [string]$Exe = 'publish\win-x64\InstaDesktop.exe',
    [string]$Report = 'artifacts\verification\notifications.json',
    [switch]$CoreOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Exe))
$reportPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Report))
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build or publish the application first.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
$testArguments = '--notification-test "' + $reportPath + '"'
if ($CoreOnly) { $testArguments += ' --notification-core-only' }
$process = Start-Process -FilePath $exePath -ArgumentList $testArguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(180000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw 'Notification test exceeded its 180-second deadline.'
}
$process.Refresh()
$result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0) { throw ('Notification test failed: ' + $result.failure) }
if (-not $result.success) { throw ('Notification test failed: ' + $result.failure) }
Write-Output ('Notification checks passed: ' + @($result.checks.PSObject.Properties).Count)
Write-Output $reportPath
