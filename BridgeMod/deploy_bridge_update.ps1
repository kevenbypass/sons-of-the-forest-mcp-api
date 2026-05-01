param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest"
)

$ErrorActionPreference = "Stop"

$sourceDir = Join-Path $PSScriptRoot "bin\Debug\net6"
$dllSource = Join-Path $sourceDir "UnityExplorerBridge.dll"
$pdbSource = Join-Path $sourceDir "UnityExplorerBridge.pdb"
$manifestSource = Join-Path $PSScriptRoot "manifest.json"

$modsDir = Join-Path $GameDir "Mods"
$manifestDir = Join-Path $modsDir "UnityExplorerBridge"
$dllTarget = Join-Path $modsDir "UnityExplorerBridge.dll"
$pdbTarget = Join-Path $modsDir "UnityExplorerBridge.pdb"
$manifestTarget = Join-Path $manifestDir "manifest.json"

if (-not (Test-Path $dllSource)) {
    throw "Build output not found: $dllSource"
}

New-Item -ItemType Directory -Force $modsDir | Out-Null
New-Item -ItemType Directory -Force $manifestDir | Out-Null

Copy-Item -LiteralPath $dllSource -Destination $dllTarget -Force
if (Test-Path $pdbSource) {
    Copy-Item -LiteralPath $pdbSource -Destination $pdbTarget -Force
}
Copy-Item -LiteralPath $manifestSource -Destination $manifestTarget -Force

Write-Host "UnityExplorerBridge atualizado em $GameDir"
