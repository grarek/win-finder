# Renders the WinFinder "Columns" app icon at every Windows icon size and packs them into
# src/WinFinder.App/Assets/WinFinder.ico (PNG-compressed entries), plus a 256px PNG preview.
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src\WinFinder.App\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

$Blue      = Color "#2563EB"
$PanelA    = Color "#DBEAFE"
$PanelB    = Color "#BFDBFE"
$RowLight  = Color "#93C5FD"
$White     = Color "#FFFFFF"
$Preview   = Color "#F59E0B"
$TextLines = Color "#CBD5E1"

function RoundRect([System.Drawing.Graphics]$g, [System.Drawing.Color]$c, [double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
    if ($r -le 0.01) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF ([single]$x), ([single]$y), ([single]$w), ([single]$h)))
    } else {
        $d = 2 * $r
        $path.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
        $path.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
        $path.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
        $path.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
        $path.CloseFigure()
    }
    $brush = New-Object System.Drawing.SolidBrush $c
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()
}

# Full design on a 120-unit grid (used for 32px and up).
function Draw-Full([System.Drawing.Graphics]$g, [double]$s, [double]$o) {
    function Box($c, $x, $y, $w, $h, $r) { RoundRect $g $c ($o + $x * $s) ($o + $y * $s) ($w * $s) ($h * $s) ($r * $s) }
    Box $Blue 0 0 120 120 26
    Box $PanelA 14 24 28 72 5
    foreach ($row in @(@(32, $RowLight), @(42, $Blue), @(52, $RowLight), @(62, $RowLight))) { Box $row[1] 18 $row[0] 20 4 2 }
    Box $PanelB 46 24 28 72 5
    foreach ($row in @(@(32, $RowLight), @(42, $RowLight), @(52, $Blue), @(62, $RowLight))) { Box $row[1] 50 $row[0] 20 4 2 }
    Box $White 78 24 28 72 5
    Box $Preview 84 32 16 16 3
    Box $TextLines 82 56 20 4 2
    Box $TextLines 82 64 14 4 2
}

# Simplified design on a 16-unit grid (used up to 24px): fine row lines vanish at this size,
# so each column keeps only its bold selection bar, and the preview keeps its swatch.
function Draw-Small([System.Drawing.Graphics]$g, [double]$s, [double]$o) {
    function Box($c, $x, $y, $w, $h, $r) { RoundRect $g $c ($o + $x * $s) ($o + $y * $s) ($w * $s) ($h * $s) ($r * $s) }
    Box $Blue 0 0 16 16 3.5
    Box $PanelA 2 3 3.6 10 1
    Box $Blue 2.6 5 2.4 1.6 0.5
    Box $PanelB 6.2 3 3.6 10 1
    Box $Blue 6.8 8 2.4 1.6 0.5
    Box $White 10.4 3 3.6 10 1
    Box $Preview 11 4.2 2.4 2.4 0.5
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($size -le 24) {
        Draw-Small $g ($size / 16.0) 0
    } else {
        $margin = [Math]::Round($size * 0.03)   # a little breathing room, like other Win11 app icons
        Draw-Full $g (($size - 2 * $margin) / 120.0) $margin
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $assets "WinFinder-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @{}
foreach ($s in $sizes) { $images[$s] = Render $s }

# ICO container: 6-byte header, 16-byte directory entry per image, then the PNG payloads.
$icoPath = Join-Path $assets "WinFinder.ico"
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$s].Length); $w.Write([UInt32]$offset)
    $offset += $images[$s].Length
}
foreach ($s in $sizes) { $w.Write($images[$s]) }
$w.Dispose(); $fs.Dispose()
"wrote $icoPath ($((Get-Item $icoPath).Length) bytes, sizes: $($sizes -join ', '))"
