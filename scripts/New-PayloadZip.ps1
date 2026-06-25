[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DestinationZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sourceFullPath = [System.IO.Path]::GetFullPath((Resolve-Path -Path $SourceDirectory).Path)
$destinationFullPath = [System.IO.Path]::GetFullPath($DestinationZip)
$destinationDirectory = Split-Path -Parent $destinationFullPath

$excludedTopLevelDirectories = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('GameData', '.launcher-data', '.launcher-updates')) {
    [void]$excludedTopLevelDirectories.Add($name)
}

$excludedFileNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('installer.log', 'launcher-errors.log')) {
    [void]$excludedFileNames.Add($name)
}

function Get-RelativeEntryPath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$FilePath
    )

    $normalizedBasePath = $BasePath.TrimEnd('\', '/')
    $normalizedFilePath = $FilePath

    if ($normalizedFilePath.StartsWith($normalizedBasePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relativePath = $normalizedFilePath.Substring($normalizedBasePath.Length).TrimStart('\', '/')
    }
    else {
        $baseUri = New-Object System.Uri(($normalizedBasePath + [System.IO.Path]::DirectorySeparatorChar))
        $fileUri = New-Object System.Uri($normalizedFilePath)
        $relativePath = [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($fileUri).ToString())
    }

    return $relativePath.Replace('\', '/')
}

function Should-IncludeFile {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File
    )

    if ($excludedFileNames.Contains($File.Name)) {
        return $false
    }

    $relativePath = Get-RelativeEntryPath -BasePath $BasePath -FilePath $File.FullName
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return $true
    }

    $firstSegment = ($relativePath -split '[\\/]')[0]
    if (-not [string]::IsNullOrWhiteSpace($firstSegment) -and $excludedTopLevelDirectories.Contains($firstSegment)) {
        return $false
    }

    return $true
}

if (-not (Test-Path -Path $sourceFullPath -PathType Container)) {
    throw "Source directory not found: $sourceFullPath"
}

if ([string]::IsNullOrWhiteSpace($destinationDirectory)) {
    throw "Destination directory is invalid: $destinationFullPath"
}

New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

$temporaryZip = [System.IO.Path]::Combine(
    $destinationDirectory,
    [System.IO.Path]::GetRandomFileName() + '.zip')

try {
    if (Test-Path -Path $temporaryZip) {
        Remove-Item -Path $temporaryZip -Force
    }

    $filesToArchive = Get-ChildItem -Path $sourceFullPath -Recurse -File | Where-Object {
        Should-IncludeFile -BasePath $sourceFullPath -File $_
    }

    $archive = [System.IO.Compression.ZipFile]::Open($temporaryZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $filesToArchive) {
            $entryPath = Get-RelativeEntryPath -BasePath $sourceFullPath -FilePath $file.FullName
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryPath,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    if (Test-Path -Path $destinationFullPath) {
        Remove-Item -Path $destinationFullPath -Force
    }

    Move-Item -Path $temporaryZip -Destination $destinationFullPath -Force
    Write-Output $destinationFullPath
}
finally {
    if (Test-Path -Path $temporaryZip) {
        Remove-Item -Path $temporaryZip -Force
    }
}
