# Builds the release: a single self-contained Chip.exe (win-x64) with assets/ beside it, then smoke-tests it
# headlessly (a hidden-window capture, no user input). Usage: powershell -File scripts/publish.ps1 [-Out artifacts/publish/win-x64] [-NoSmoke]
param([string]$Out = "artifacts/publish/win-x64", [switch]$NoSmoke)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$outFull = Join-Path $root $Out
if (Test-Path $outFull) { Remove-Item -Recurse -Force $outFull }
dotnet publish src/Chip.Editor -p:PublishProfile=win-x64 -o $outFull --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $outFull "Chip.exe"
if (-not (Test-Path $exe)) { throw "Chip.exe missing from $outFull" }
$extra = Get-ChildItem $outFull -File | Where-Object { $_.Name -ne "Chip.exe" }
if ($extra) { Write-Warning ("Loose files beside Chip.exe: " + ($extra.Name -join ", ")) }
if (-not (Test-Path (Join-Path $outFull "assets/fonts/IBMPlexMono-Regular.ttf"))) { throw "assets were not copied" }

if (-not $NoSmoke) {
    $shot = Join-Path $root "artifacts/publish/smoke.png"
    $env:CHIP_UI_SCALE = "1"
    # GUI-subsystem exe: wait explicitly so the exit code is real.
    $p = Start-Process -FilePath $exe -ArgumentList @("--shot", "`"$(Join-Path $root 'tests/fixtures/level.chip')`"", "`"$shot`"", "objects") -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0 -or -not (Test-Path $shot)) { throw "published build failed its smoke capture ($($p.ExitCode))" }
    Write-Host "Smoke capture: $shot"
}
$mb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "OK: $exe ($mb MB) with assets/"
