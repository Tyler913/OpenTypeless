# Renders src/OpenTypeless/Assets/AppIcon.ico (and AppIcon.png): a waveform glyph on a rounded gradient
# tile, the same artwork as the macOS scripts/make-icon.swift, at every size Windows asks for.
# Run with Windows PowerShell (it uses System.Drawing):  powershell -File scripts\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$assets = Join-Path $PSScriptRoot '..\src\OpenTypeless\Assets'

# Relative bar heights of the waveform glyph (shared with the tray icon and the in-app tile).
$bars = @(0.26, 0.6, 1.0, 0.55, 0.82, 0.33)

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(0.5, $size * 0.04)
    $w = $size - 2 * $inset
    $r = $w * 0.225
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($inset, $inset, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($inset + $w - 2 * $r, $inset, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($inset + $w - 2 * $r, $inset + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($inset, $inset + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, $w, $w
    $c1 = [System.Drawing.Color]::FromArgb(255, 92, 77, 242)    # (0.36, 0.30, 0.95)
    $c2 = [System.Drawing.Color]::FromArgb(255, 31, 158, 250)   # (0.12, 0.62, 0.98)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, 60.0
    $g.FillPath($brush, $path)

    # Waveform: glyph box 50% of the tile, rounded bars.
    $glyph = $w * 0.5
    $count = $bars.Count
    $pitch = $glyph / ($count - 0.35)
    $barWidth = [Math]::Max(1.0, $pitch * 0.5)
    $x0 = $size / 2 - ($pitch * ($count - 1)) / 2
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $barWidth
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    for ($i = 0; $i -lt $count; $i++) {
        $h = [Math]::Max(0.0, $glyph * $bars[$i] - $barWidth)
        $x = $x0 + $i * $pitch
        $g.DrawLine($pen, [single]$x, [single]($size / 2 - $h / 2), [single]$x, [single]($size / 2 + $h / 2))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$images = @{}
foreach ($s in $sizes) { $images[$s] = Render $s }

# ICO container with PNG-compressed entries (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$s].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$s].Length
}
foreach ($s in $sizes) { $bw.Write($images[$s]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.png'), (Render 512))
Write-Host "OK $assets\AppIcon.ico"
