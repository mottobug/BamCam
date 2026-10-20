# Generates the BamCam application icon (PNG + multi-size ICO) with System.Drawing.
# Windows only; the generated files are committed, so this only needs to run when the design changes.
#   powershell -ExecutionPolicy Bypass -File build/make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\BamCam.App\Assets"))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Rounded square background (view background colour).
    $r = 48 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(8 * $s, 8 * $s, $r, $r, 180, 90)
    $path.AddArc((248 * $s) - $r, 8 * $s, $r, $r, 270, 90)
    $path.AddArc((248 * $s) - $r, (248 * $s) - $r, $r, $r, 0, 90)
    $path.AddArc(8 * $s, (248 * $s) - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 50, 50, 60))), $path)

    # Toolpath: blue line with a green arc, like the drawing view.
    $blue = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 70, 120, 235)), ([Math]::Max(2, 14 * $s))
    $green = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0, 220, 0)), ([Math]::Max(2, 14 * $s))
    $blue.StartCap = $blue.EndCap = $green.StartCap = $green.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($blue, 40 * $s, 200 * $s, 128 * $s, 200 * $s)
    $g.DrawArc($green, 88 * $s, 120 * $s, 128 * $s, 80 * $s, 0, 90)
    $g.DrawLine($blue, 216 * $s, 160 * $s, 216 * $s, 60 * $s)

    # End mill: shank and fluted tip.
    $steel = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 230, 232, 238))
    $flute = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 50, 50, 60)), ([Math]::Max(1, 7 * $s))
    $g.FillRectangle($steel, 104 * $s, 28 * $s, 48 * $s, 70 * $s)
    $g.FillRectangle($steel, 110 * $s, 98 * $s, 36 * $s, 62 * $s)
    $g.DrawLine($flute, 112 * $s, 112 * $s, 144 * $s, 128 * $s)
    $g.DrawLine($flute, 112 * $s, 134 * $s, 144 * $s, 150 * $s)
    $g.Dispose()
    return $bmp
}

$big = New-IconBitmap 256
$big.Save((Join-Path $OutDir "bamcam.png"), [System.Drawing.Imaging.ImageFormat]::Png)

# ICO with PNG-compressed images (supported since Windows Vista).
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "bamcam.ico"), $out.ToArray())
Write-Host "Icon written to $OutDir"
