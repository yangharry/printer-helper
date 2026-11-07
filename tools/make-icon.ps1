# Draws the app icon (src/PrinterHelper/app.ico). Run with Windows PowerShell 5.1:
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\PrinterHelper\app.ico'
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

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $blue = [System.Drawing.ColorTranslator]::FromHtml('#2563EB')
    $light = [System.Drawing.ColorTranslator]::FromHtml('#DBEAFE')
    $dark = [System.Drawing.ColorTranslator]::FromHtml('#1E3A8A')
    $white = [System.Drawing.Color]::White

    $g.FillPath((New-Object System.Drawing.SolidBrush $blue), (New-RoundedRect 0 0 $s $s ($s * 0.2)))
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
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
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
