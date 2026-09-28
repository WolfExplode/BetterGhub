# Renders the BetterGhub app icon with WPF and packs it into a multi-size .ico (PNG frames).
# Usage: .\tools\make-icon.ps1   (writes src\BetterGhub\Assets\BetterGhub.ico and a 256px preview PNG)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\BetterGhub\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Render-Frame([int]$size) {
    $s = $size / 256.0
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $gradient = New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.Color]::FromRgb(0x2E, 0xC5, 0xEA), [System.Windows.Media.Color]::FromRgb(0x17, 0x6B, 0xD1), 45.0)
    $radius = 56 * $s
    $dc.DrawRoundedRectangle($gradient, $null, [System.Windows.Rect]::new((8 * $s), (8 * $s), (240 * $s), (240 * $s)), $radius, $radius)

    # Mouse body: a tall rounded shape, dark on the gradient.
    $dark = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0x08, 0x16, 0x1D))
    $body = [System.Windows.Rect]::new((76 * $s), (44 * $s), (104 * $s), (168 * $s))
    $dc.DrawRoundedRectangle($dark, $null, $body, 52 * $s, 52 * $s)

    # Button split and wheel in light cyan.
    $light = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0x8F, 0xE6, 0xF7))
    $pen = [System.Windows.Media.Pen]::new($light, [Math]::Max(1.0, (7 * $s)))
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    $dc.DrawLine($pen, [System.Windows.Point]::new((128 * $s), (52 * $s)), [System.Windows.Point]::new((128 * $s), (78 * $s)))
    $dc.DrawLine($pen, [System.Windows.Point]::new((128 * $s), (126 * $s)), [System.Windows.Point]::new((128 * $s), (118 * $s)))
    $wheel = [System.Windows.Rect]::new((118 * $s), (84 * $s), (20 * $s), (34 * $s))
    $dc.DrawRoundedRectangle($light, $null, $wheel, 10 * $s, 10 * $s)
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @{}
foreach ($size in $sizes) { $frames[$size] = Render-Frame $size }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
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
Write-Host "Wrote $(Join-Path $assets 'BetterGhub.ico')"
