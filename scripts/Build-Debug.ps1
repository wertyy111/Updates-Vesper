param(
    [switch]$BuildFrontend
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$userBuildRoot = Join-Path $env:LOCALAPPDATA 'VesperLauncherBuild'
$solutionPath = Get-ChildItem -Path $repoRoot -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName
$sdkVersion = '10.0.301'
$systemDotnetRoot = 'C:\Program Files\dotnet'
$localDotnetRoot = Join-Path $env:LOCALAPPDATA 'VesperCodexTools\dotnet-sdk'
$dotnetRoot = if (Test-Path (Join-Path $systemDotnetRoot "sdk\$sdkVersion\Sdks")) { $systemDotnetRoot } else { $localDotnetRoot }
$sdkRoot = Join-Path $dotnetRoot "sdk\$sdkVersion"

if (-not (Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) {
    throw "Local .NET SDK was not found: $dotnetRoot. Install the SDK or ask Codex to reinstall it."
}

if (-not (Test-Path (Join-Path $sdkRoot 'Sdks'))) {
    throw "Local .NET SDK MSBuild Sdks were not found: $sdkRoot."
}

New-Item -ItemType Directory -Force -Path $userBuildRoot | Out-Null

$env:DOTNET_ROOT = $dotnetRoot
$env:DOTNET_HOST_PATH = Join-Path $dotnetRoot 'dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet_home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:VESPER_USER_BUILD_ROOT = $userBuildRoot
$env:PATH = "$dotnetRoot;$env:PATH"

$skipFrontend = if ($BuildFrontend) { 'false' } else { 'true' }

& (Join-Path $dotnetRoot 'dotnet.exe') build $solutionPath `
    -c Debug `
    -nologo `
    -p:SkipFrontendBuild=$skipFrontend
