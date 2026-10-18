# Proof bar for every branch: build, run all tests, then headless UI captures of the standard states.
# Usage: powershell -File scripts/verify.ps1 [-Out docs/ui/verify] [-Scale 1.5] [-Run "cmd.a,cmd.b"]
param([string]$Out = "artifacts/verify", [string]$Scale = "1.5", [string]$Run = "")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
dotnet test --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "tests failed" }
# Per-checkout build folder so parallel worktrees never share binaries.
$hash = [BitConverter]::ToString([Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($root))).Replace("-", "").Substring(0, 10)
$bin = Join-Path $env:TEMP "chip-verify-$hash"
dotnet build src/Chip.Editor -o $bin --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "editor build failed" }
New-Item -ItemType Directory -Force $Out | Out-Null
$level = Join-Path $root "tests/fixtures/level.chip"
$env:CHIP_UI_SCALE = $Scale
foreach ($state in "empty", "objects", "faces", "block", "shortcuts") {
    $src = if ($state -eq "empty") { "-" } else { $level }
    $shotArgs = @("--shot", $src, (Join-Path $Out "$state.png"), $state)
    if ($Run) { $shotArgs += @("--run", $Run) }
    # Chip.exe is a GUI app: without the pipe PowerShell returns at once and $LASTEXITCODE is stale.
    & (Join-Path $bin "Chip.exe") @shotArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "capture '$state' failed ($LASTEXITCODE)" }
    if (-not (Test-Path (Join-Path $Out "$state.png"))) { throw "capture '$state' wrote no image" }
}
Write-Host "OK: tests passed, captures in $Out"
