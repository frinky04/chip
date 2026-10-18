# Proof bar for every branch: build, run all tests, then headless UI captures of the standard states.
# Usage: powershell -File scripts/verify.ps1 [-Out docs/ui/verify] [-Scale 1.5] [-Run "cmd.a,cmd.b"]
param([string]$Out = "artifacts/verify", [string]$Scale = "1.5", [string]$Run = "")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
dotnet test --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "tests failed" }
$bin = Join-Path $env:TEMP "chip-verify-build"
dotnet build src/Chip.Editor -o $bin --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "editor build failed" }
New-Item -ItemType Directory -Force $Out | Out-Null
$level = Join-Path $root "tests/fixtures/level.chip"
$env:CHIP_UI_SCALE = $Scale
foreach ($state in "empty", "objects", "faces", "block", "shortcuts") {
    $src = if ($state -eq "empty") { "-" } else { $level }
    $shotArgs = @("--shot", $src, (Join-Path $Out "$state.png"), $state)
    if ($Run) { $shotArgs += @("--run", $Run) }
    & (Join-Path $bin "Chip.exe") @shotArgs
    if ($LASTEXITCODE -ne 0) { throw "capture '$state' failed ($LASTEXITCODE)" }
}
Write-Host "OK: tests passed, captures in $Out"
