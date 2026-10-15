param([string]$OutputPath = "$PSScriptRoot\..\DuplicateVideoCleaner\assets\DuplicateVideoCleaner.ico")

Add-Type -AssemblyName System.Drawing
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = $size / 256.0
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $blue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(64,139,230))
    $dark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(24,34,46))
    $light = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(238,244,251), [Math]::Max(1, 10*$scale))
    $light.StartCap = $light.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.FillRectangle($blue, 0, 0, $size, $size)
    $graphics.FillRectangle($dark, 42*$scale, 55*$scale, 140*$scale, 112*$scale)
    $graphics.DrawRectangle($light, 42*$scale, 55*$scale, 140*$scale, 112*$scale)
    $graphics.DrawLine($light, 70*$scale, 55*$scale, 70*$scale, 167*$scale)
    $graphics.DrawLine($light, 154*$scale, 55*$scale, 154*$scale, 167*$scale)
    $points = @([System.Drawing.PointF]::new(98*$scale,91*$scale),[System.Drawing.PointF]::new(136*$scale,111*$scale),[System.Drawing.PointF]::new(98*$scale,133*$scale))
    $graphics.FillPolygon([System.Drawing.Brushes]::White, $points)
    $graphics.FillEllipse($dark, 126*$scale, 125*$scale, 78*$scale, 78*$scale)
    $graphics.DrawEllipse($light, 126*$scale, 125*$scale, 78*$scale, 78*$scale)
    $graphics.DrawLine($light, 194*$scale, 193*$scale, 224*$scale, 223*$scale)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $blue.Dispose(); $dark.Dispose(); $light.Dispose()
}
$directory = Split-Path -Parent $OutputPath
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$file = [System.IO.File]::Create($OutputPath)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
for ($i=0; $i -lt $images.Count; $i++) {
    $size = $sizes[$i]
    $writer.Write([byte]($(if($size -eq 256){0}else{$size})))
    $writer.Write([byte]($(if($size -eq 256){0}else{$size})))
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Dispose(); $file.Dispose()
