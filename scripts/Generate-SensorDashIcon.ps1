# Generate the Win32/MSI SensorDash application icon using the same design
# as TrayIconFactory: cyan telemetry pulse on a navy monitoring dial.
# Creates a standard multi-resolution PNG-compressed ICO (16/24/32/48/64/256).
param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $scale = [single]($Size / 64.0)
            $graphics.ScaleTransform($scale, $scale)

            $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(17, 34, 49))
            $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(83, 207, 202), [single]4.5)
            $faint = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(55, 108, 125), [single]1.6)
            $pulse = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(230, 250, 249), [single]5.2)
            try {
                $outline.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
                $pulse.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pulse.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pulse.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                $graphics.FillEllipse($background, [System.Drawing.RectangleF]::new(3, 3, 58, 58))
                $graphics.DrawEllipse($outline, [System.Drawing.RectangleF]::new(3.5, 3.5, 57, 57))
                $graphics.DrawArc($faint, [System.Drawing.RectangleF]::new(10.5, 10.5, 43, 43), [single]210, [single]120)
                [System.Drawing.PointF[]]$points = @(
                    [System.Drawing.PointF]::new(13,33), [System.Drawing.PointF]::new(22,33),
                    [System.Drawing.PointF]::new(28,20), [System.Drawing.PointF]::new(35,45),
                    [System.Drawing.PointF]::new(41,28), [System.Drawing.PointF]::new(46,33),
                    [System.Drawing.PointF]::new(51,33)
                )
                $graphics.DrawLines($pulse, $points)
            }
            finally {
                $background.Dispose(); $outline.Dispose(); $faint.Dispose(); $pulse.Dispose()
            }
        }
        finally { $graphics.Dispose() }
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally { $stream.Dispose() }
    }
    finally { $bitmap.Dispose() }
}

$sizes = @(16,24,32,48,64,256)
$frames = foreach ($size in $sizes) {
    [pscustomobject]@{ Size = $size; Image = [byte[]](New-IconPng $size) }
}
$parent = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $parent)) {
    [void](New-Item -ItemType Directory -Force -Path $parent)
}
$file = [System.IO.File]::Create($OutputPath)
try {
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)  # reserved
        $writer.Write([uint16]1)  # ICO
        $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)      # palette
            $writer.Write([byte]0)      # reserved
            $writer.Write([uint16]1)    # planes
            $writer.Write([uint16]32)   # bit depth
            $writer.Write([uint32]$frame.Image.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Image.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Image) }
    }
    finally { $writer.Dispose() }
}
finally { $file.Dispose() }
Write-Host "Generated SensorDash icon: $OutputPath ($((Get-Item $OutputPath).Length) bytes)"
