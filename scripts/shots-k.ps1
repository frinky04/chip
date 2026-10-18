# Headless captures of the app-shell windows (stream K): palette, preferences pages, crash recovery.
# Usage: powershell -File scripts/shots-k.ps1 [-Out artifacts/verify/k] [-Scale 1.5]   (run scripts/verify.ps1 first to build)
param([string]$Out = "artifacts/verify/k", [string]$Scale = "1.5")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$hash = [BitConverter]::ToString([Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($root))).Replace("-", "").Substring(0, 10)
$bin = Join-Path $env:TEMP "chip-verify-$hash"
dotnet build src/Chip.Editor -o $bin --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "editor build failed" }
New-Item -ItemType Directory -Force $Out | Out-Null
$level = Join-Path $root "tests/fixtures/level.chip"
$env:CHIP_UI_SCALE = $Scale
foreach ($state in "palette", "palette-search", "preferences", "prefs-viewport", "prefs-hotkeys", "prefs-paths", "recovery", "rescale") {
    # Chip is a GUI-subsystem exe: wait for it explicitly so exit codes are real.
    $p = Start-Process -FilePath (Join-Path $bin "Chip.exe") -ArgumentList @("--shot", "`"$level`"", "`"$(Join-Path $Out "$state.png")`"", $state) -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { throw "capture '$state' failed ($($p.ExitCode))" }
}
Write-Host "OK: shell captures in $Out"
