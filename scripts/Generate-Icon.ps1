$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $repoRoot 'assets'
New-Item -ItemType Directory -Force -Path $assetDirectory | Out-Null
$outputPath = Join-Path $assetDirectory 'DjShrek-Draiver.ico'

function New-DjShrekIconBytes {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $scale = $Size / 256.0
        $graphics.ScaleTransform([single]$scale, [single]$scale)

        $background = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 9, 20, 40))
        $panel = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 15, 43, 74))
        $cyan = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 104, 225, 255))
        $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 35, 174, 229), 9)
        $shieldPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 83, 205, 248), 10)
        try {
            $graphics.FillEllipse($background, 12, 12, 232, 232)
            $graphics.DrawEllipse($ringPen, 35, 35, 186, 186)
            $innerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 32, 75, 125), 5)
            try { $graphics.DrawEllipse($innerPen, 53, 53, 150, 150) } finally { $innerPen.Dispose() }

            $points = [System.Drawing.Point[]]@(
                (New-Object System.Drawing.Point(128, 55)),
                (New-Object System.Drawing.Point(183, 78)),
                (New-Object System.Drawing.Point(176, 144)),
                (New-Object System.Drawing.Point(160, 178)),
                (New-Object System.Drawing.Point(128, 205)),
                (New-Object System.Drawing.Point(96, 178)),
                (New-Object System.Drawing.Point(80, 144)),
                (New-Object System.Drawing.Point(73, 78))
            )
            $graphics.FillPolygon($panel, $points)
            $graphics.DrawPolygon($shieldPen, $points)

            # Geometric D monogram in cyan.
            $graphics.FillRectangle($cyan, 97, 94, 24, 68)
            $graphics.FillRectangle($cyan, 113, 94, 40, 22)
            $graphics.FillRectangle($cyan, 113, 140, 40, 22)
            $graphics.FillRectangle($cyan, 143, 108, 22, 40)
            # Cut the center to form a clear letter rather than a solid block.
            $cutout = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 15, 43, 74))
            try { $graphics.FillRectangle($cutout, 121, 116, 22, 24) } finally { $cutout.Dispose() }

            foreach ($point in @(@(128, 27), @(128, 229), @(27, 128), @(229, 128))) {
                $graphics.FillEllipse($cyan, ([single]$point[0] - 4), ([single]$point[1] - 4), 8, 8)
            }

            $stream = New-Object System.IO.MemoryStream
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                return ,$stream.ToArray()
            } finally { $stream.Dispose() }
        } finally {
            $background.Dispose()
            $panel.Dispose()
            $cyan.Dispose()
            $ringPen.Dispose()
            $shieldPen.Dispose()
        }
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 32, 48, 256)
$images = @()
foreach ($size in $sizes) {
    $images += [pscustomobject]@{ Size = $size; Bytes = (New-DjShrekIconBytes -Size $size) }
}

$stream = [System.IO.File]::Open($outputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$images.Count)
    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { [byte]0 } else { [byte]$image.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) { $writer.Write($image.Bytes) }
    $writer.Flush()
} finally {
    $writer.Dispose()
    $stream.Dispose()
}

$iconBytes = [System.IO.File]::ReadAllBytes($outputPath)
if ($iconBytes.Length -lt 22 -or $iconBytes[0] -ne 0 -or $iconBytes[1] -ne 0 -or $iconBytes[2] -ne 1) {
    throw "Generated ICO header is invalid."
}
Write-Host "Created icon: $outputPath ($($iconBytes.Length) bytes)"