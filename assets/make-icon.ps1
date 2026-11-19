$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$png = Join-Path $root "controller.png"
$ico = Join-Path $root "controller.ico"
if (-not (Test-Path $png)) {
    throw "Missing $png"
}

function New-SquareBitmap($source, $size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.DrawImage($source, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $graphics.Dispose()
    return $bitmap
}

function Get-IconImageBytes($bitmap) {
    $size = $bitmap.Width
    $xorSize = $size * $size * 4
    $maskStride = [int]([Math]::Floor(($size + 31) / 32) * 4)
    $maskSize = $maskStride * $size
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([uint32]40)
    $writer.Write([int32]$size)
    $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]0)
    $writer.Write([uint32]$xorSize)
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([uint32]0)
    $writer.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $color = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$color.B)
            $writer.Write([byte]$color.G)
            $writer.Write([byte]$color.R)
            $writer.Write([byte]$color.A)
        }
    }
    $mask = New-Object byte[] $maskSize
    $writer.Write($mask)
    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
    if ($bytes.Length -ne (40 + $xorSize + $maskSize)) {
        throw "Icon image size mismatch for $size"
    }
    return ,$bytes
}

$source = New-Object System.Drawing.Bitmap $png
$sizes = @(48, 32, 16)
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) {
    $square = New-SquareBitmap $source $size
    $images.Add((Get-IconImageBytes $square))
    $square.Dispose()
}
$source.Dispose()

$count = $images.Count
$offset = 6 + (16 * $count)
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$count)
for ($n = 0; $n -lt $count; $n++) {
    $dimension = $sizes[$n]
    $stored = if ($dimension -ge 256) { 0 } else { $dimension }
    $writer.Write([byte]$stored)
    $writer.Write([byte]$stored)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$n].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$n].Length
}
foreach ($image in $images) {
    $payload = [byte[]]$image
    if ($payload.Length -lt 40) {
        throw "Icon payload is only $($payload.Length) bytes"
    }
    $writer.Write($payload)
}
$writer.Flush()
[System.IO.File]::WriteAllBytes($ico, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()
Write-Host "Wrote $ico"
