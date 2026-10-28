# Tests the Godot import addon in godot/ (the addon's dev project) headless: manifest generation, entity processing, and
# a real Chip export of tests/fixtures/entities.chip imported end to end.
# Usage: powershell -File scripts/godot-test.ps1 [-Godot path\to\Godot_console.exe] [-UpdateBuiltins]
# -UpdateBuiltins regenerates src/Chip.Core/Scene/BuiltinEntities.json (Chip's classes outside a Godot project) from the
# addon's scenes; do it after changing godot/addons/chip_import/entities.
param([string]$Godot = $env:GODOT, [switch]$UpdateBuiltins)
# Godot prints warnings on stderr; failures are read from exit codes, so stderr mustn't stop the script.
$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $Godot) { $Godot = (Get-Command godot -ErrorAction SilentlyContinue).Source }
if (-not $Godot -or -not (Test-Path $Godot)) { throw "Godot not found: pass -Godot <path to the console executable> or set GODOT" }
$project = Join-Path $root "godot"

# Godot is a GUI app on Windows: the pipe makes PowerShell wait for it and set $LASTEXITCODE.
& $Godot --headless --path $project --import 2>&1 | Out-Null
& $Godot --headless --path $project --script res://addons/chip_import/chip_manifest_cli.gd 2>&1 | Write-Host
if ($LASTEXITCODE -ne 0) { throw "manifest generation failed" }
if ($UpdateBuiltins) {
    & $Godot --headless --path $project --script res://addons/chip_import/chip_manifest_cli.gd -- --builtins (Join-Path $root "src/Chip.Core/Scene/BuiltinEntities.json") 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "built-in classes failed" }
}

$glb = Join-Path $project "tests/out/entities.glb"
New-Item -ItemType Directory -Force (Split-Path $glb) | Out-Null
dotnet run --project src/Chip.Editor -v q -- --export tests/fixtures/entities.chip $glb
if ($LASTEXITCODE -ne 0) { throw "Chip export failed" }
$meshGlb = Join-Path $project "tests/out/mesh_entities.glb"
dotnet run --project src/Chip.Editor -v q -- --export tests/fixtures/mesh_entities.chip $meshGlb
if ($LASTEXITCODE -ne 0) { throw "Chip export failed" }

& $Godot --headless --path $project --script res://tests/test_chip_import.gd -- --glb $glb --mesh-glb $meshGlb 2>&1 | Write-Host
if ($LASTEXITCODE -ne 0) { throw "addon tests failed" }
Write-Host "OK: addon tests passed"
