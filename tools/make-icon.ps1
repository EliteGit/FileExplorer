# Draws the Fathom icon (a deep-water gradient tile holding a treemap mosaic) and writes a multi-size .ico.
# Usage: powershell -File tools\make-icon.ps1 [-Out FileExplorer.App\app.ico] [-Preview preview.png]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\FileExplorer.App\app.ico'),
    [string]$Preview = ''
)
Add-Type -AssemblyName System.Drawing

function New-Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.5, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size

    # tile: deep navy to teal
    $tile = New-Rounded ($s * 0.04) ($s * 0.04) ($s * 0.92) ($s * 0.92) ($s * 0.21)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s),
        [System.Drawing.Color]::FromArgb(255, 16, 42, 102), [System.Drawing.Color]::FromArgb(255, 14, 165, 164))
    $g.FillPath($grad, $tile)

    # soft top highlight
    $hi = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, ($s * 0.5)),
        [System.Drawing.Color]::FromArgb(70, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.SetClip($tile); $g.FillRectangle($hi, 0, 0, $s, $s * 0.5); $g.ResetClip()

    # treemap mosaic (x, y, w, h as fractions of the tile) with the app's own type colours
    $cells = @(
        @(0.16, 0.16, 0.36, 0.68, '#E0903A'),
        @(0.54, 0.16, 0.30, 0.30, '#4AA98A'),
        @(0.54, 0.48, 0.15, 0.36, '#B565C9'),
        @(0.71, 0.48, 0.13, 0.17, '#4F7CD9'),
        @(0.71, 0.67, 0.13, 0.17, '#E6EAF1')
    )
    $gap = [Math]::Max(0.6, $s * 0.012)
    foreach ($c in $cells) {
        $x = $s * $c[0] + $gap; $y = $s * $c[1] + $gap; $w = $s * $c[2] - 2 * $gap; $h = $s * $c[3] - 2 * $gap
        if ($w -lt 1 -or $h -lt 1) { continue }
        $path = New-Rounded $x $y $w $h ($s * 0.035)
        $col = [System.Drawing.ColorTranslator]::FromHtml($c[4])
        $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(238, $col))
        $g.FillPath($fill, $path)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = Draw-Icon $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256 -and $Preview) { $b.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    ,$ms.ToArray()
}

$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) | ForEach-Object { Join-Path $_.Path (Split-Path $Out -Leaf) }))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); $fs.Close()
Write-Host "Wrote $Out ($($sizes.Count) sizes)"
