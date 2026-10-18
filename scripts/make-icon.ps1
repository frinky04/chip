# Draws Chip's app icon (an orange outline cube on a dark rounded tile, after Tabler's "cube" glyph) at every size
# and writes src/Chip.Editor/chip.ico (PNG-compressed entries) plus assets/icon/chip-{32,256}.png for the window icon.
# Each size is rendered natively (not downscaled) with stroke widths tuned so 16 px stays legible.
# Usage: powershell -File scripts/make-icon.ps1   (Windows PowerShell 5.1: uses System.Drawing)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile: dark panel colour with a rounded corner radius of ~18%.
    $r = [Math]::Max(2, $size * 0.18)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2; $e = $size - 0.5
    $tile.AddArc(0, 0, $d, $d, 180, 90); $tile.AddArc($e - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($e - $d, $e - $d, $d, $d, 0, 90); $tile.AddArc(0, $e - $d, $d, $d, 90, 90); $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x21, 0x21, 0x24))), $tile)

    # Cube in Tabler's 24-unit space, mapped into the tile with a margin.
    $margin = if ($size -le 16) { 0.08 } else { 0.14 }
    $s = $size * (1 - 2 * $margin) / 18.0
    $o = $size * $margin - 3 * $s
    function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([float]($o + $x * $s)), ([float]($o + $y * $s)) }
    $stroke = if ($size -le 16) { 1.6 } elseif ($size -le 32) { [Math]::Max(2, $size * 0.075) } else { $size * 0.07 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xFF, 0x8A, 0x1F)), ([float]$stroke)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hex = [System.Drawing.PointF[]]@((P 12 3), (P 20 7.5), (P 20 16.5), (P 12 21), (P 4 16.5), (P 4 7.5))
    $g.DrawPolygon($pen, $hex)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((P 4 7.5), (P 12 12), (P 20 7.5)))
    $g.DrawLine($pen, (P 12 12), (P 12 21))
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @{}
foreach ($size in $sizes) { $pngs[$size] = New-IconPng $size }

# ICO: 6-byte header, 16-byte directory entries, then the PNG blobs.
$icoPath = Join-Path $root "src/Chip.Editor/chip.ico"
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $dim = if ($size -ge 256) { 0 } else { $size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$size].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$size].Length
}
foreach ($size in $sizes) { $w.Write($pngs[$size]) }
$w.Close()

$iconDir = Join-Path $root "assets/icon"
New-Item -ItemType Directory -Force $iconDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $iconDir "chip-32.png"), $pngs[32])
[System.IO.File]::WriteAllBytes((Join-Path $iconDir "chip-256.png"), $pngs[256])
Write-Host "Wrote $icoPath and assets/icon/chip-{32,256}.png"
