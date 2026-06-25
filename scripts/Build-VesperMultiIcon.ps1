param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,

    [Parameter(Mandatory = $true)]
    [string]$OutputIco,

    [double]$PaddingRatio = 0.10
)

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

function Get-CroppedBitmapSource {
    param(
        [System.Windows.Media.Imaging.BitmapSource]$Source,
        [byte]$AlphaThreshold = 8
    )

    $width = $Source.PixelWidth
    $height = $Source.PixelHeight
    $stride = $width * 4
    $pixels = New-Object byte[] ($stride * $height)
    $Source.CopyPixels($pixels, $stride, 0)

    $minX = $width
    $minY = $height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $height; $y++) {
        for ($x = 0; $x -lt $width; $x++) {
            $index = ($y * $stride) + ($x * 4)
            $alpha = $pixels[$index + 3]
            if ($alpha -gt $AlphaThreshold) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    if ($maxX -lt $minX -or $maxY -lt $minY) {
        return $Source
    }

    $cropWidth = $maxX - $minX + 1
    $cropHeight = $maxY - $minY + 1
    $cropped = [System.Windows.Media.Imaging.CroppedBitmap]::new(
        $Source,
        [System.Windows.Int32Rect]::new($minX, $minY, $cropWidth, $cropHeight))
    $cropped.Freeze()
    return $cropped
}

function New-IconFramePngBytes {
    param(
        [System.Windows.Media.Imaging.BitmapSource]$Source,
        [int]$Size,
        [double]$PaddingRatio
    )

    $drawingVisual = [System.Windows.Media.DrawingVisual]::new()
    $drawingContext = $drawingVisual.RenderOpen()

    $drawingContext.DrawRectangle(
        [System.Windows.Media.Brushes]::Transparent,
        $null,
        [System.Windows.Rect]::new(0, 0, $Size, $Size))

    $padding = [Math]::Round($Size * $PaddingRatio)
    $availableWidth = $Size - ($padding * 2)
    $availableHeight = $Size - ($padding * 2)

    $scale = [Math]::Min($availableWidth / $Source.PixelWidth, $availableHeight / $Source.PixelHeight)
    $renderWidth = $Source.PixelWidth * $scale
    $renderHeight = $Source.PixelHeight * $scale
    $offsetX = ($Size - $renderWidth) / 2.0
    $offsetY = ($Size - $renderHeight) / 2.0

    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode(
        $drawingVisual,
        [System.Windows.Media.BitmapScalingMode]::Fant)

    $drawingContext.DrawImage(
        $Source,
        [System.Windows.Rect]::new($offsetX, $offsetY, $renderWidth, $renderHeight))

    $drawingContext.Close()

    $target = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($drawingVisual)
    $target.Freeze()

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target))

    $memory = [System.IO.MemoryStream]::new()
    $encoder.Save($memory)
    $bytes = $memory.ToArray()
    $memory.Dispose()
    return $bytes
}

function Write-IcoFile {
    param(
        [hashtable[]]$Frames,
        [string]$OutputPath
    )

    $outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    }

    $stream = [System.IO.File]::Create($OutputPath)
    $writer = [System.IO.BinaryWriter]::new($stream)

    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$Frames.Count)

        $offset = 6 + (16 * $Frames.Count)
        foreach ($frame in $Frames) {
            $size = [int]$frame.Size
            $pngBytes = [byte[]]$frame.Bytes

            $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
            $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$pngBytes.Length)
            $writer.Write([UInt32]$offset)

            $offset += $pngBytes.Length
        }

        foreach ($frame in $Frames) {
            $writer.Write([byte[]]$frame.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

if (-not (Test-Path $SourcePng)) {
    throw "Source image not found: $SourcePng"
}

$bitmap = [System.Windows.Media.Imaging.BitmapImage]::new()
$bitmap.BeginInit()
$bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$bitmap.UriSource = [System.Uri]::new($SourcePng)
$bitmap.EndInit()
$bitmap.Freeze()
$bitmap = Get-CroppedBitmapSource -Source $bitmap

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    @{
        Size = $size
        Bytes = New-IconFramePngBytes -Source $bitmap -Size $size -PaddingRatio $PaddingRatio
    }
}

Write-IcoFile -Frames $frames -OutputPath $OutputIco
Write-Output "ICO written: $OutputIco"
