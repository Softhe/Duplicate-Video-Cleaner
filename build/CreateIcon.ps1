# Draws the app icon: viewfinder corners around a play mark, with a faded duplicate behind it.
# Outputs (in DuplicateVideoCleaner\assets):
#   DuplicateVideoCleaner.ico   white glyph on a dark rounded tile: EXE, shortcuts, installer
#   GlyphOnDark.ico             white glyph, transparent: title bar / taskbar on dark backgrounds
#   GlyphOnLight.ico            near-black glyph, transparent: title bar / taskbar on light backgrounds
#   DuplicateVideoCleaner.png   256 px preview of the tile icon
# Geometry is in a 256-unit box and matches DuplicateVideoCleaner.svg.
param([string]$AssetsPath = "$PSScriptRoot\..\DuplicateVideoCleaner\assets")

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$ink = [System.Drawing.Color]::FromArgb(255, 27, 27, 27)
$tile = [System.Drawing.Color]::FromArgb(255, 26, 26, 26)

function New-Triangle([double]$x, [double]$y, [double]$h) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($x, $y),
        [System.Drawing.PointF]::new($x, $y + $h),
        [System.Drawing.PointF]::new($x + $h * 0.87, $y + $h / 2)))
    return $path
}

function New-Corners {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $segments = @(
        @{ L1 = @(28, 86, 28, 58); Arc = @(28, 28, 180); L2 = @(58, 28, 86, 28) },
        @{ L1 = @(170, 28, 198, 28); Arc = @(168, 28, 270); L2 = @(228, 58, 228, 86) },
        @{ L1 = @(228, 170, 228, 198); Arc = @(168, 168, 0); L2 = @(198, 228, 170, 228) },
        @{ L1 = @(86, 228, 58, 228); Arc = @(28, 168, 90); L2 = @(28, 198, 28, 170) })
    foreach ($s in $segments) {
        $path.StartFigure()
        $path.AddLine([float]$s.L1[0], [float]$s.L1[1], [float]$s.L1[2], [float]$s.L1[3])
        $path.AddArc([float]$s.Arc[0], [float]$s.Arc[1], 60, 60, [float]$s.Arc[2], 90)
        $path.AddLine([float]$s.L2[0], [float]$s.L2[1], [float]$s.L2[2], [float]$s.L2[3])
    }
    return $path
}

function New-Pen([System.Drawing.Color]$color, [double]$width) {
    $pen = [System.Drawing.Pen]::new($color, [float]$width)
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $pen
}

# The glyph on a transparent bitmap. $box is the glyph's pixel size; small sizes thicken strokes
# and drop the faded duplicate, which turns into noise below 20 px.
function New-Glyph([int]$box, [System.Drawing.Color]$color) {
    $bitmap = [System.Drawing.Bitmap]::new($box, $box, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $unit = $box / 256.0
    $g.ScaleTransform([float]$unit, [float]$unit)
    $minimum = 1.6 / $unit                     # strokes never thinner than 1.6 px
    $corner = [Math]::Max(18, $minimum)
    $round = [Math]::Max(16, $minimum)
    $front = New-Triangle 90 98 80
    if ($box -ge 20) {
        # Draw the duplicate opaque on its own layer, then blend the layer at 40 %, so the fill and
        # its rounding stroke do not double up where they overlap.
        $layer = [System.Drawing.Bitmap]::new($box, $box, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $lg = [System.Drawing.Graphics]::FromImage($layer)
        $lg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $lg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $lg.Clear([System.Drawing.Color]::Transparent)
        $lg.ScaleTransform([float]$unit, [float]$unit)
        $ghost = New-Triangle 118 70 80
        $brush = [System.Drawing.SolidBrush]::new($color); $pen = New-Pen $color $round
        $lg.FillPath($brush, $ghost); $lg.DrawPath($pen, $ghost)
        $brush.Dispose(); $pen.Dispose(); $ghost.Dispose(); $lg.Dispose()
        $matrix = [System.Drawing.Imaging.ColorMatrix]::new()
        $matrix.Matrix33 = 0.4
        $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
        $attributes.SetColorMatrix($matrix)
        $g.ResetTransform()
        $g.DrawImage($layer, [System.Drawing.Rectangle]::new(0, 0, $box, $box), 0, 0, $box, $box, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        $g.ScaleTransform([float]$unit, [float]$unit)
        $attributes.Dispose(); $layer.Dispose()
        # Clear a transparent gap around the front play mark.
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $gap = New-Pen ([System.Drawing.Color]::Transparent) ($round + [Math]::Max(14, 2 / $unit))
        $clear = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::Transparent)
        $g.FillPath($clear, $front); $g.DrawPath($gap, $front)
        $gap.Dispose(); $clear.Dispose()
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    }
    $brush = [System.Drawing.SolidBrush]::new($color)
    $pen = New-Pen $color $round
    $g.FillPath($brush, $front); $g.DrawPath($pen, $front)
    $corners = New-Corners
    $cornerPen = New-Pen $color $corner
    $g.DrawPath($cornerPen, $corners)
    $brush.Dispose(); $pen.Dispose(); $cornerPen.Dispose(); $corners.Dispose(); $front.Dispose(); $g.Dispose()
    return $bitmap
}

function New-RoundedRectangle([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2 * $r
    $path.AddArc([float]$x, [float]$y, [float]$d, [float]$d, 180, 90)
    $path.AddArc([float]($x + $w - $d), [float]$y, [float]$d, [float]$d, 270, 90)
    $path.AddArc([float]($x + $w - $d), [float]($y + $h - $d), [float]$d, [float]$d, 0, 90)
    $path.AddArc([float]$x, [float]($y + $h - $d), [float]$d, [float]$d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Image([int]$size, [string]$kind) {
    if ($kind -eq 'tile') {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $margin = [Math]::Max(0, [Math]::Round($size * 0.04))
        $plate = New-RoundedRectangle $margin $margin ($size - 2 * $margin) ($size - 2 * $margin) ($size * 0.22)
        $brush = [System.Drawing.SolidBrush]::new($tile)
        $g.FillPath($brush, $plate)
        # A faint light rim keeps the dark tile visible on dark desktops and taskbars.
        $rim = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(46, 255, 255, 255), [float][Math]::Max(1, $size / 96))
        $g.DrawPath($rim, $plate)
        $rim.Dispose()
        $inset = [Math]::Round($size * 0.18)
        $glyph = New-Glyph ($size - 2 * $inset) $white
        $g.DrawImage($glyph, [int]$inset, [int]$inset, $glyph.Width, $glyph.Height)
        $glyph.Dispose(); $brush.Dispose(); $plate.Dispose(); $g.Dispose()
        return $bitmap
    }
    $color = if ($kind -eq 'dark') { $white } else { $ink }
    # A small margin keeps the corners off the edge where Windows may crop.
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.Clear([System.Drawing.Color]::Transparent)
    $inset = [Math]::Round($size * 0.04)
    $glyph = New-Glyph ($size - 2 * $inset) $color
    $g.DrawImage($glyph, [int]$inset, [int]$inset, $glyph.Width, $glyph.Height)
    $glyph.Dispose(); $g.Dispose()
    return $bitmap
}

function Write-Icon([string]$path, [string]$kind) {
    $images = foreach ($size in $sizes) {
        $bitmap = New-Image $size $kind
        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        , $stream.ToArray()
        $stream.Dispose()
    }
    $file = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($file)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Dispose(); $file.Dispose()
}

[System.IO.Directory]::CreateDirectory($AssetsPath) | Out-Null
Write-Icon (Join-Path $AssetsPath 'DuplicateVideoCleaner.ico') 'tile'
Write-Icon (Join-Path $AssetsPath 'GlyphOnDark.ico') 'dark'
Write-Icon (Join-Path $AssetsPath 'GlyphOnLight.ico') 'light'
$preview = New-Image 256 'tile'
$preview.Save((Join-Path $AssetsPath 'DuplicateVideoCleaner.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
