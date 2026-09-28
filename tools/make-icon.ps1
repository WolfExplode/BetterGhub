# Renders the app icon from assets\artwork\logitech.svg with WPF and packs a multi-size .ico (PNG frames).
# Usage: .\tools\make-icon.ps1
# Writes src\BetterGhub\Assets\BetterGhub.ico, icon-256.png (logo on a dark tile) and logo-256.png (logo only).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\BetterGhub\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# Pull the path data and fill colour out of the SVG, then load it as a WPF geometry.
$svg = Get-Content -Raw -LiteralPath (Join-Path $root 'assets\artwork\logitech.svg')
$data = [regex]::Match($svg, '\sd="([^"]+)"').Groups[1].Value
$fill = [regex]::Match($svg, 'fill="(#[0-9a-fA-F]{6})"').Groups[1].Value
if (-not $data) { throw 'No path data found in logitech.svg' }
# SVG allows "1.5.5" for "1.5 .5"; add the separators WPF's parser expects.
$data = [regex]::Replace($data, '(\.\d+)(?=\.)', '$1 ')
$geometry = [System.Windows.Media.Geometry]::Parse($data)
$bounds = $geometry.Bounds
$logoBrush = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString($fill))

function Render-Frame([int]$size, [bool]$tile) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $inset = 0.0
    if ($tile) {
        $tileBrush = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x10, 0x13, 0x18))
        $radius = $size * 0.22
        $dc.DrawRoundedRectangle($tileBrush, $null, [System.Windows.Rect]::new(0, 0, $size, $size), $radius, $radius)
        $inset = $size * 0.19
    }
    # Fit the logo into the square, centred.
    $box = $size - 2 * $inset
    $scale = $box / [Math]::Max($bounds.Width, $bounds.Height)
    $group = [System.Windows.Media.TransformGroup]::new()
    $group.Children.Add([System.Windows.Media.TranslateTransform]::new((-$bounds.X), (-$bounds.Y)))
    $group.Children.Add([System.Windows.Media.ScaleTransform]::new($scale, $scale))
    $group.Children.Add([System.Windows.Media.TranslateTransform]::new(($inset + ($box - $bounds.Width * $scale) / 2), ($inset + ($box - $bounds.Height * $scale) / 2)))
    $dc.PushTransform($group)
    $dc.DrawGeometry($logoBrush, $null, $geometry)
    $dc.Pop()
    $dc.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @{}
foreach ($size in $sizes) { $frames[$size] = Render-Frame $size $true }

$out = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($out)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $bytes = $frames[$size]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$bytes.Length); $writer.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($size in $sizes) { $writer.Write($frames[$size]) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'BetterGhub.ico'), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon-256.png'), $frames[256])
[System.IO.File]::WriteAllBytes((Join-Path $assets 'logo-256.png'), (Render-Frame 256 $false))
Write-Host "Wrote icon files to $assets"
