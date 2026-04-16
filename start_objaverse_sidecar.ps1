$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sidecarDir = Join-Path $repoRoot "Assets\StreamingAssets\ObjaverseSidecar"
$appPath = Join-Path $sidecarDir "app.py"

if (-not (Test-Path $appPath)) {
    throw "Objaverse sidecar not found at $appPath"
}

Write-Host "Starting Objaverse sidecar from $sidecarDir"
& python $appPath
