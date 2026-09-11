$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconDirectory = Join-Path $PSScriptRoot '..\Assets\Icons'
[IO.Directory]::CreateDirectory($iconDirectory) | Out-Null
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $bitmap = New-Object Drawing.Bitmap($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(8, 8, 80, 80, 180, 90)
    $path.AddArc(168, 8, 80, 80, 270, 90)
    $path.AddArc(168, 168, 80, 80, 0, 90)
    $path.AddArc(8, 168, 80, 80, 90, 90)
    $path.CloseFigure()
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush([Drawing.Point]::new(0, 256), [Drawing.Point]::new(256, 0), [Drawing.Color]::FromArgb(255, 230, 0), [Drawing.Color]::FromArgb(131, 58, 180))
    $blend = New-Object Drawing.Drawing2D.ColorBlend
    $blend.Positions = [single[]](0, 0.35, 0.7, 1)
    $blend.Colors = [Drawing.Color[]]@([Drawing.Color]::FromArgb(255, 230, 0), [Drawing.Color]::FromArgb(253, 29, 29), [Drawing.Color]::FromArgb(225, 48, 108), [Drawing.Color]::FromArgb(131, 58, 180))
    $brush.InterpolationColors = $blend
    $graphics.FillPath($brush, $path)
    $pen = New-Object Drawing.Pen([Drawing.Color]::White, 14)
    $camera = New-Object Drawing.Drawing2D.GraphicsPath
    $camera.AddArc(58, 58, 48, 48, 180, 90)
    $camera.AddArc(150, 58, 48, 48, 270, 90)
    $camera.AddArc(150, 150, 48, 48, 0, 90)
    $camera.AddArc(58, 150, 48, 48, 90, 90)
    $camera.CloseFigure()
    $graphics.DrawPath($pen, $camera)
    $graphics.DrawEllipse($pen, 94, 94, 68, 68)
    $graphics.FillEllipse([Drawing.Brushes]::White, 165, 77, 15, 15)
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose(); $camera.Dispose(); $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $iconDirectory 'app.ico'))
$writer = New-Object IO.BinaryWriter($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
$writer.Dispose(); $file.Dispose()
