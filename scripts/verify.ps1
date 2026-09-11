param(
    [string]$Exe = 'publish\win-x64\InstaDesktop.exe',
    [string]$Report = 'artifacts\verification\stable-smoke.json'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Exe))
$reportPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Report))
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build or publish the application first.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
$process = Start-Process -FilePath $exePath -ArgumentList ('--smoke-test "' + $reportPath + '"') -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(180000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw 'Smoke test exceeded its 180-second deadline.'
}
$process.Refresh()
if ($process.ExitCode -ne 0) { throw "Smoke test failed. Inspect $reportPath and its isolated profile logs." }
$result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $result.success) { throw 'Smoke test reported failure.' }
Write-Output "Smoke test passed: $reportPath"
$result.checks
