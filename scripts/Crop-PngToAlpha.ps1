param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,

    [Parameter(Mandatory = $true)]
    [string]$OutputPng,

    [int]$AlphaThreshold = 8
)

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$resolvedSource = (Resolve-Path -LiteralPath $SourcePng).Path
$sourceStream = [System.IO.File]::OpenRead($resolvedSource)

try {
    $decoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
        $sourceStream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)

    $bitmap = $decoder.Frames[0]
    $width = $bitmap.PixelWidth
    $height = $bitmap.PixelHeight
    $stride = $width * 4
    $pixels = New-Object byte[] ($stride * $height)
    $bitmap.CopyPixels($pixels, $stride, 0)

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
        throw "Image appears fully transparent: $resolvedSource"
    }

    $cropWidth = $maxX - $minX + 1
    $cropHeight = $maxY - $minY + 1
    $cropped = [System.Windows.Media.Imaging.CroppedBitmap]::new(
        $bitmap,
        [System.Windows.Int32Rect]::new($minX, $minY, $cropWidth, $cropHeight))
    $cropped.Freeze()

    $resolvedOutput = $OutputPng
    if (Test-Path -LiteralPath $OutputPng) {
        $resolvedOutput = (Resolve-Path -LiteralPath $OutputPng).Path
    }

    $outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    }

    $finalOutputPath = $resolvedOutput
    $temporaryOutputPath = $resolvedOutput
    if ([string]::Equals($resolvedSource, $resolvedOutput, [System.StringComparison]::OrdinalIgnoreCase)) {
        $temporaryOutputPath = [System.IO.Path]::Combine(
            $outputDirectory,
            ([System.IO.Path]::GetFileNameWithoutExtension($resolvedOutput) + ".cropped.tmp" + [System.IO.Path]::GetExtension($resolvedOutput)))
    }

    $targetStream = [System.IO.File]::Create($temporaryOutputPath)
    try {
        $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($cropped))
        $encoder.Save($targetStream)
    }
    finally {
        $targetStream.Dispose()
    }

    if (-not [string]::Equals($temporaryOutputPath, $finalOutputPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        [System.IO.File]::Copy($temporaryOutputPath, $finalOutputPath, $true)
        [System.IO.File]::Delete($temporaryOutputPath)
    }

    Write-Output "Cropped PNG written: $finalOutputPath ($cropWidth x $cropHeight)"
}
finally {
    $sourceStream.Dispose()
}
