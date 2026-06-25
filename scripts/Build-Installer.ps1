[CmdletBinding()]
param(
    [string]$ProjectPath = 'windows/installer-src/Installer.csproj',
    [string]$VelopackSetupPath,
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputRoot = '_build_verify/setup-wrapper',
    [switch]$UpdateWindowsSetup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Resolve-RepoRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ([System.IO.Path]::IsPathRooted($expanded)) {
        return [System.IO.Path]::GetFullPath($expanded)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $expanded))
}

function Find-LatestVelopackSetup {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $releaseRoot = Join-Path $RepoRoot '_build_verify/velopack'
    if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
        return $null
    }

    return Get-ChildItem -LiteralPath $releaseRoot -Filter 'Vesper.Launcher-win-Setup.exe' -File -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

$repoRoot = Resolve-NormalizedPath (Join-Path $PSScriptRoot '..')
$projectFullPath = Resolve-RepoRelativePath -RepoRoot $repoRoot -Path $ProjectPath
if (-not (Test-Path -LiteralPath $projectFullPath -PathType Leaf)) {
    throw "Installer wrapper project not found: $projectFullPath"
}

$resolvedVelopackSetupPath = if ([string]::IsNullOrWhiteSpace($VelopackSetupPath)) {
    Find-LatestVelopackSetup -RepoRoot $repoRoot
}
else {
    Resolve-RepoRelativePath -RepoRoot $repoRoot -Path $VelopackSetupPath
}

if ([string]::IsNullOrWhiteSpace($resolvedVelopackSetupPath) -or -not (Test-Path -LiteralPath $resolvedVelopackSetupPath -PathType Leaf)) {
    throw 'Velopack setup was not found. Run Publish-VelopackUpdate.ps1 first or pass -VelopackSetupPath.'
}

$outputRootFullPath = Resolve-RepoRelativePath -RepoRoot $repoRoot -Path $OutputRoot
$publishDirectory = Join-Path $outputRootFullPath 'publish'
$intermediateDirectory = Join-Path $outputRootFullPath 'obj'
$nugetPackages = Join-Path $repoRoot '_build_verify/dotnet/nuget'

New-Item -ItemType Directory -Path $publishDirectory, $intermediateDirectory, $nugetPackages -Force | Out-Null

Write-Host "Building Vesper setup wrapper around:"
Write-Host $resolvedVelopackSetupPath

& dotnet publish $projectFullPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $publishDirectory `
    -nologo `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    "-p:VelopackSetup=$resolvedVelopackSetupPath" `
    "-p:BaseIntermediateOutputPath=$intermediateDirectory/" `
    "-p:RestorePackagesPath=$nugetPackages"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedSetupPath = Join-Path $publishDirectory 'VesperLauncherSetup.exe'
if (-not (Test-Path -LiteralPath $publishedSetupPath -PathType Leaf)) {
    throw "Published setup wrapper not found: $publishedSetupPath"
}

if ($UpdateWindowsSetup) {
    $windowsSetupPath = Join-Path $repoRoot 'windows/setup.exe'
    Copy-Item -LiteralPath $publishedSetupPath -Destination $windowsSetupPath -Force
    Write-Host "Updated: $windowsSetupPath"
}

$item = Get-Item -LiteralPath $publishedSetupPath
Write-Host ''
Write-Host "Installer wrapper ready: $publishedSetupPath"
Write-Host ("Size: {0:N0} bytes" -f $item.Length)
