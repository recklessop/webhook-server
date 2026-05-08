<#
.SYNOPSIS
    Generates webhook-server.ico (multi-resolution) and webhook-server.png from
    a programmatic design. Re-run after changing Draw-Icon to refresh assets.

.DESCRIPTION
    Renders the icon at 16/24/32/48/64/128/256 px using System.Drawing, then
    assembles a Microsoft-format ICO file with each size embedded as PNG. No
    external tools required.

    Design: a rounded-square teal background (#0E7C66) with a stylized white
    hook shape (a "J"-like curve with an arrow tip).
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\resources')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Background: rounded square in brand teal.
    $bgColor = [System.Drawing.Color]::FromArgb(0xFF, 0x0E, 0x7C, 0x66)
    $bgBrush = New-Object System.Drawing.SolidBrush $bgColor
    $radius = [int]($size * 0.22)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bgBrush, $path)

    # Foreground: white hook shape - a thick curved stroke shaped like a "J"
    # tipped with an arrowhead, sized relative to the canvas.
    $fgColor = [System.Drawing.Color]::White
    $stroke = [Math]::Max(2, [int]($size * 0.12))
    $pen = New-Object System.Drawing.Pen $fgColor, $stroke
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Hook curve: vertical down-stroke on the right, then a half-circle arc
    # curling to the left and ending in a small filled dot for the hook tip.
    $cx     = [single]($size * 0.62)
    $top    = [single]($size * 0.22)
    $bottom = [single]($size * 0.58)
    $arcD   = [single]($size * 0.34)        # arc diameter
    $arcLeft = [single]($cx - $arcD)        # left edge of arc circle

    # Vertical stroke from (cx, top) to (cx, bottom).
    $g.DrawLine($pen, $cx, $top, $cx, $bottom)

    # Half-circle arc beneath: starts at (cx, bottom), curls to (cx - arcD, bottom).
    $arcRect = New-Object System.Drawing.RectangleF $arcLeft, ([single]($bottom - $arcD / 2)), $arcD, $arcD
    $g.DrawArc($pen, $arcRect, 0, 180)

    # Filled circle at the tip end of the arc.
    $tipR = [single]($stroke * 0.6)
    $tipX = $arcLeft
    $tipY = [single]($bottom)
    $brushFg = New-Object System.Drawing.SolidBrush $fgColor
    $g.FillEllipse($brushFg, [single]($tipX - $tipR), [single]($tipY - $tipR), [single]($tipR * 2), [single]($tipR * 2))

    $brushFg.Dispose(); $pen.Dispose(); $bgBrush.Dispose(); $path.Dispose()
    $g.Dispose()
    return $bmp
}

# Generate each size as PNG bytes. Hashtable keys are prefixed with "s" because
# PowerShell hashtable lookups by integer key behave inconsistently with PSObject
# wrapping; string keys round-trip cleanly.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @{}
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs["s$s"] = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
}

# Save the master 256 PNG separately for places that need a transparent PNG.
$pngPath = Join-Path $OutputDir 'webhook-server.png'
[System.IO.File]::WriteAllBytes($pngPath, [byte[]]$pngs['s256'])

# Assemble multi-resolution ICO.
$icoPath = Join-Path $OutputDir 'webhook-server.ico'
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
try {
    $count = $sizes.Count
    $bw.Write([UInt16]0)            # idReserved
    $bw.Write([UInt16]1)            # idType: 1 = ICO
    $bw.Write([UInt16]$count)       # idCount

    # Directory entries (16 bytes each).
    $offset = 6 + 16 * $count
    foreach ($s in $sizes) {
        $bytes = $pngs["s$s"]
        $w = if ($s -ge 256) { 0 } else { $s }
        $h = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$w)         # width
        $bw.Write([byte]$h)         # height
        $bw.Write([byte]0)          # colorCount
        $bw.Write([byte]0)          # reserved
        $bw.Write([UInt16]1)        # planes
        $bw.Write([UInt16]32)       # bitCount
        $bw.Write([UInt32]$bytes.Length)
        $bw.Write([UInt32]$offset)
        $offset += $bytes.Length
    }

    # Image data.
    foreach ($s in $sizes) { $bw.Write($pngs["s$s"]) }

    $bw.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
}
finally {
    $bw.Dispose(); $ms.Dispose()
}

Write-Host "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes)"
Write-Host "Wrote $pngPath ($((Get-Item $pngPath).Length) bytes)"
