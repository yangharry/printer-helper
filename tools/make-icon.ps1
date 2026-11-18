# Draws the app icon: src/PrinterHelper/app.ico for Windows and packaging/macos/icon-1024.png for the Mac
# (packaging/macos/make-icns.sh turns that into the .icns). Run with Windows PowerShell 5.1:
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\PrinterHelper\app.ico'
$macOut = Join-Path $PSScriptRoot '..\packaging\macos\icon-1024.png'
$sizes = 16, 24, 32, 48, 64, 256

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Canvas([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    return $bmp, $g
}

# The artwork in an s x s square at the graphics origin; $radius rounds the blue background.
function Add-Artwork($g, [single]$s, [single]$radius) {
    $blue = [System.Drawing.ColorTranslator]::FromHtml('#2563EB')
    $light = [System.Drawing.ColorTranslator]::FromHtml('#DBEAFE')
    $dark = [System.Drawing.ColorTranslator]::FromHtml('#1E3A8A')
    $white = [System.Drawing.Color]::White

    $g.FillPath((New-Object System.Drawing.SolidBrush $blue), (New-RoundedRect 0 0 $s $s $radius))
    # paper going in
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $white), $s * 0.30, $s * 0.15, $s * 0.40, $s * 0.28)
    # printer body
    $g.FillPath((New-Object System.Drawing.SolidBrush $light), (New-RoundedRect ($s * 0.13) ($s * 0.36) ($s * 0.74) ($s * 0.30) ($s * 0.07)))
    # output slot
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $dark), $s * 0.24, $s * 0.555, $s * 0.52, [Math]::Max(1, $s * 0.045))
    # page coming out, with text lines
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $white), $s * 0.28, $s * 0.585, $s * 0.44, $s * 0.28)
    if ($s -ge 32) {
        $pen = New-Object System.Drawing.Pen $blue, ([single][Math]::Max(1, $s * 0.035))
        foreach ($y in 0.66, 0.73, 0.80) { $g.DrawLine($pen, $s * 0.34, $s * $y, $s * 0.66, $s * $y) }
    }
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

function New-IconPng([int]$s) {
    $bmp, $g = New-Canvas $s
    Add-Artwork $g $s ($s * 0.2)
    $g.Dispose()
    $png = Get-PngBytes $bmp
    $bmp.Dispose()
    return , $png
}

# Apple's macOS icon grid: the artwork fills a centred 824 px rounded square (corner about 185 px) on a
# transparent 1024 px canvas, over a soft shadow, so it sits like the system's icons in the Dock and Finder.
function New-MacIconPng {
    $canvas = 1024; $art = 824; $inset = ($canvas - $art) / 2; $radius = $art * 0.225
    $bmp, $g = New-Canvas $canvas
    # Stacked, growing, faint copies of the shape make a shadow that fades out over about 24 px.
    $shadowLayers = 24
    for ($i = $shadowLayers; $i -ge 1; $i--) {
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(3, 0, 0, 0))
        $g.FillPath($brush, (New-RoundedRect ($inset - $i) ($inset - $i + 10) ($art + 2 * $i) ($art + 2 * $i) ($radius + $i)))
    }
    $g.TranslateTransform($inset, $inset)
    Add-Artwork $g $art $radius
    $g.Dispose()
    $png = Get-PngBytes $bmp
    $bmp.Dispose()
    return , $png
}

$images = foreach ($s in $sizes) { , (New-IconPng $s) }

# ICO container: header, one directory entry per size, then the PNG data.
$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes((Resolve-Path (Split-Path $out)).Path + '\app.ico', $file.ToArray())
"wrote $out ($($file.Length) bytes)"

$mac = New-MacIconPng
[IO.File]::WriteAllBytes((Resolve-Path (Split-Path $macOut)).Path + '\icon-1024.png', $mac)
"wrote $macOut ($($mac.Length) bytes)"
