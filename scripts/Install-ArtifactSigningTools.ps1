[CmdletBinding()]
param(
    [string]$OutputDirectory = '%LOCALAPPDATA%\VesperLauncher\ArtifactSigningTools',
    [string]$WindowsSdkBuildToolsVersion,
    [string]$ArtifactSigningClientVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    return [System.IO.Path]::GetFullPath($expanded)
}

function Get-LatestNuGetPackageVersion {
    param([Parameter(Mandatory = $true)][string]$PackageId)

    $lowerId = $PackageId.ToLowerInvariant()
    $indexUri = "https://api.nuget.org/v3-flatcontainer/$lowerId/index.json"
    $index = Invoke-RestMethod -Method 'GET' -Uri $indexUri
    if ($null -eq $index -or $null -eq $index.versions -or $index.versions.Count -eq 0) {
        throw "Could not resolve latest version for NuGet package: $PackageId"
    }

    return $index.versions[-1]
}

function Install-NuGetPackageExtract {
    param(
        [Parameter(Mandatory = $true)][string]$PackageId,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    $lowerId = $PackageId.ToLowerInvariant()
    $packageUri = "https://api.nuget.org/v3-flatcontainer/$lowerId/$Version/$lowerId.$Version.nupkg"
    $packageDirectory = Join-Path $DestinationRoot "$PackageId.$Version"
    $packageArchivePath = Join-Path $DestinationRoot "$PackageId.$Version.nupkg"
    $packageZipPath = Join-Path $DestinationRoot "$PackageId.$Version.zip"

    if (Test-Path -LiteralPath $packageDirectory) {
        Remove-Item -LiteralPath $packageDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
    Invoke-WebRequest -Method 'GET' -Uri $packageUri -OutFile $packageArchivePath
    Copy-Item -LiteralPath $packageArchivePath -Destination $packageZipPath -Force
    Expand-Archive -LiteralPath $packageZipPath -DestinationPath $packageDirectory -Force
    Remove-Item -LiteralPath $packageArchivePath -Force
    Remove-Item -LiteralPath $packageZipPath -Force

    return $packageDirectory
}

$outputRoot = Resolve-NormalizedPath $OutputDirectory
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($WindowsSdkBuildToolsVersion)) {
    $WindowsSdkBuildToolsVersion = Get-LatestNuGetPackageVersion -PackageId 'Microsoft.Windows.SDK.BuildTools'
}

if ([string]::IsNullOrWhiteSpace($ArtifactSigningClientVersion)) {
    $ArtifactSigningClientVersion = Get-LatestNuGetPackageVersion -PackageId 'Microsoft.ArtifactSigning.Client'
}

$sdkDirectory = Install-NuGetPackageExtract -PackageId 'Microsoft.Windows.SDK.BuildTools' -Version $WindowsSdkBuildToolsVersion -DestinationRoot $outputRoot
$artifactDirectory = Install-NuGetPackageExtract -PackageId 'Microsoft.ArtifactSigning.Client' -Version $ArtifactSigningClientVersion -DestinationRoot $outputRoot

$signTool = Get-ChildItem -LiteralPath $sdkDirectory -Recurse -Filter 'signtool.exe' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Select-Object -First 1
if ($null -eq $signTool) {
    $signTool = Get-ChildItem -LiteralPath $sdkDirectory -Recurse -Filter 'signtool.exe' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

$dlib = Get-ChildItem -LiteralPath $artifactDirectory -Recurse -Filter 'Azure.CodeSigning.Dlib.dll' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Select-Object -First 1
if ($null -eq $dlib) {
    $dlib = Get-ChildItem -LiteralPath $artifactDirectory -Recurse -Filter 'Azure.CodeSigning.Dlib.dll' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

if ($null -eq $signTool) {
    throw 'signtool.exe was not found after extracting Microsoft.Windows.SDK.BuildTools.'
}

if ($null -eq $dlib) {
    throw 'Azure.CodeSigning.Dlib.dll was not found after extracting Microsoft.ArtifactSigning.Client.'
}

[pscustomobject]@{
    OutputDirectory = $outputRoot
    WindowsSdkBuildToolsVersion = $WindowsSdkBuildToolsVersion
    ArtifactSigningClientVersion = $ArtifactSigningClientVersion
    SignToolPath = $signTool.FullName
    TrustedSigningDlibPath = $dlib.FullName
}
