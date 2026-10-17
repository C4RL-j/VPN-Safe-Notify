param([string]$OutputPath = (Join-Path $PSScriptRoot 'App.ico'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Retain the application's existing blue shield and warning mark. Each ICO frame
# is drawn as vectors at four times its target size and downsampled for clean edges.
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $large = New-Object System.Drawing.Bitmap ($size * 4), ($size * 4)
    $graphics = [System.Drawing.Graphics]::FromImage($large)
    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(145, 184, 255))
    $mark = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(24, 32, 48)), 3
    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(24, 32, 48))
    $bitmap = $null
    $resize = $null
    $stream = New-Object System.IO.MemoryStream
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.ScaleTransform(($size * 4 / 32.0), ($size * 4 / 32.0))
        $points = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(16, 2), [System.Drawing.PointF]::new(29, 7),
            [System.Drawing.PointF]::new(27, 21), [System.Drawing.PointF]::new(22, 27),
            [System.Drawing.PointF]::new(16, 31), [System.Drawing.PointF]::new(10, 27),
            [System.Drawing.PointF]::new(5, 21), [System.Drawing.PointF]::new(3, 7))
        $shield.AddLines($points)
        $shield.CloseFigure()
        $graphics.FillPath($fill, $shield)
        $mark.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $mark.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($mark, 16, 10, 16, 18)
        $graphics.FillEllipse($dot, 14.5, 22, 3, 3)

        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $resize = [System.Drawing.Graphics]::FromImage($bitmap)
        $resize.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $resize.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $resize.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $resize.DrawImage($large, 0, 0, $size, $size)
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += [PSCustomObject]@{ Size = $size; Bytes = $stream.ToArray() }
    }
    finally {
        $stream.Dispose()
        if ($resize) { $resize.Dispose() }
        if ($bitmap) { $bitmap.Dispose() }
        $graphics.Dispose()
        $large.Dispose()
        $shield.Dispose()
        $fill.Dispose()
        $mark.Dispose()
        $dot.Dispose()
    }
}

$file = [System.IO.File]::Create([System.IO.Path]::GetFullPath($OutputPath))
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose(); $file.Dispose() }
