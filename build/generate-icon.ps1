<#
.SYNOPSIS
    Generates src/WindowsAudioSwitcher/Assets/app.ico from primitives.

.DESCRIPTION
    Renders a stylised speaker icon at 16, 24, 32, 48, 64, 128 and 256 px,
    encodes each frame as PNG, and assembles a multi-resolution ICO.

    Run once when the icon design changes. The resulting app.ico is checked
    into the repo and referenced by the .csproj (ApplicationIcon + Resource),
    the WPF MainWindow, the tray icon loader, and the Inno Setup script.
#>

[CmdletBinding()] param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Render-Icon {
    param([int]$size)

    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # All coordinates designed at 256, scaled to the target size.
    $s = $size / 256.0

    # Rounded square background (Material Blue 600).
    $bgColor = [System.Drawing.Color]::FromArgb(255, 30, 136, 229)
    $bgBrush = New-Object System.Drawing.SolidBrush $bgColor
    $radius = 44 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $radius
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bgBrush, $path)

    # Speaker body (white): rectangle + triangular cone.
    $white = [System.Drawing.Brushes]::White
    $points = New-Object 'System.Drawing.PointF[]' 6
    $points[0] = New-Object System.Drawing.PointF (56 * $s),  (98 * $s)
    $points[1] = New-Object System.Drawing.PointF (100 * $s), (98 * $s)
    $points[2] = New-Object System.Drawing.PointF (148 * $s), (54 * $s)
    $points[3] = New-Object System.Drawing.PointF (148 * $s), (202 * $s)
    $points[4] = New-Object System.Drawing.PointF (100 * $s), (158 * $s)
    $points[5] = New-Object System.Drawing.PointF (56 * $s),  (158 * $s)
    $g.FillPolygon($white, $points)

    # Sound waves: two arcs to the right of the speaker.
    if ($size -ge 24) {
        $penWidth = [Math]::Max(2.0, 14 * $s)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $penWidth
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

        # Inner arc (always shown above 24px).
        $g.DrawArc($pen, (162 * $s), (96 * $s), (36 * $s), (64 * $s), -50, 100)

        if ($size -ge 48) {
            # Outer arc (only on larger sizes — at 16/24 it'd just be noise).
            $g.DrawArc($pen, (172 * $s), (72 * $s), (60 * $s), (112 * $s), -50, 100)
        }
        $pen.Dispose()
    }

    $g.Dispose()
    $bgBrush.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

function Write-Ico {
    param([string]$path, [hashtable]$frames)

    $sizes = @($frames.Keys | Sort-Object)
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    try {
        # ICONDIR
        $bw.Write([uint16]0)             # reserved
        $bw.Write([uint16]1)             # type: 1 = ICO
        $bw.Write([uint16]$sizes.Count)  # frame count

        # ICONDIRENTRY x N
        $offset = 6 + 16 * $sizes.Count
        foreach ($sz in $sizes) {
            $bytes = $frames[$sz]
            $w = if ($sz -ge 256) { 0 } else { $sz }
            $bw.Write([byte]$w)          # width  (0 means 256)
            $bw.Write([byte]$w)          # height (0 means 256)
            $bw.Write([byte]0)           # palette colours
            $bw.Write([byte]0)           # reserved
            $bw.Write([uint16]1)         # planes
            $bw.Write([uint16]32)        # bits per pixel
            $bw.Write([uint32]$bytes.Length)
            $bw.Write([uint32]$offset)
            $offset += $bytes.Length
        }

        # Image data. Use BaseStream.Write to avoid PowerShell's overload
        # resolution picking BinaryWriter.Write(byte) when given a byte[].
        $bw.Flush()
        foreach ($sz in $sizes) {
            $bytes = $frames[$sz]
            $fs.Write($bytes, 0, $bytes.Length)
        }
    } finally {
        $bw.Dispose()
        $fs.Dispose()
    }
}

$root    = Split-Path -Parent $PSScriptRoot
$assets  = Join-Path $root 'src\WindowsAudioSwitcher\Assets'
$outFile = Join-Path $assets 'app.ico'
if (-not (Test-Path $assets)) { New-Item -ItemType Directory -Path $assets | Out-Null }

$frames = @{}
foreach ($sz in 16, 24, 32, 48, 64, 128, 256) {
    Write-Host "  rendering ${sz}x${sz}"
    $frames[$sz] = Render-Icon -size $sz
}

Write-Ico -path $outFile -frames $frames
Write-Host ("Wrote {0} ({1:N0} bytes, {2} frames)" -f $outFile, (Get-Item $outFile).Length, $frames.Count) -ForegroundColor Green
