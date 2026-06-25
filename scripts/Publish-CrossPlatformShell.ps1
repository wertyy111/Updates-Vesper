param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string[]]$RuntimeIdentifiers = @('linux-x64', 'osx-x64', 'osx-arm64'),

    [switch]$SelfContained,

    [switch]$BuildFrontend
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path (Join-Path $repoRoot 'VesperLauncher') 'VesperLauncher.CrossPlatform.csproj'
$outputRoot = Join-Path $repoRoot '_build_verify/cross-platform-publish'
$nugetRoot = Join-Path $repoRoot '_build_verify/nuget-packages'
$temporaryRoot = Join-Path $repoRoot '_build_verify/tmp'
$httpCacheRoot = Join-Path $repoRoot '_build_verify/nuget-http-cache'
$dotnetHomeRoot = Join-Path $repoRoot '_build_verify/dotnet-home'
$userBuildRoot = Join-Path $repoRoot '_build_verify/user-build-root'

if (-not (Test-Path $projectPath)) {
    throw "Cross-platform project was not found: $projectPath"
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $nugetRoot | Out-Null
New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
New-Item -ItemType Directory -Force -Path $httpCacheRoot | Out-Null
New-Item -ItemType Directory -Force -Path $dotnetHomeRoot | Out-Null
New-Item -ItemType Directory -Force -Path $userBuildRoot | Out-Null

$env:TMP = $temporaryRoot
$env:TEMP = $temporaryRoot
$env:NUGET_PACKAGES = $nugetRoot
$env:NUGET_HTTP_CACHE_PATH = $httpCacheRoot
$env:DOTNET_CLI_HOME = $dotnetHomeRoot
$env:VESPER_USER_BUILD_ROOT = $userBuildRoot

foreach ($rid in $RuntimeIdentifiers) {
    $publishDirectory = Join-Path $outputRoot $rid
    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

    $arguments = @(
        'publish',
        $projectPath,
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(),
        "-p:RestorePackagesPath=$nugetRoot",
        "-p:SkipFrontendBuild=$(-not $BuildFrontend)",
        '-o', $publishDirectory,
        '-nologo'
    )

    Write-Host "Publishing Vesper Launcher shell for $rid..."
    & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $rid with exit code $LASTEXITCODE."
    }
}

Write-Host "Cross-platform shell publish completed: $outputRoot"

