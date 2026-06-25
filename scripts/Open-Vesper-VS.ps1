param(
    [string]$SolutionPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$userBuildRoot = Join-Path $env:LOCALAPPDATA 'VesperLauncherBuild'
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
$env:MSBuildSDKsPath = Join-Path $sdkRoot 'Sdks'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:VESPER_USER_BUILD_ROOT = $userBuildRoot
$env:PATH = "$dotnetRoot;$env:PATH"

if ([string]::IsNullOrWhiteSpace($SolutionPath)) {
    $SolutionPath = Get-ChildItem -Path $repoRoot -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName
}

$resolvedSolution = Resolve-Path $SolutionPath
$runningDevenv = Get-Process devenv -ErrorAction SilentlyContinue |
    Where-Object { $_.Path } |
    Select-Object -First 1 -ExpandProperty Path

$vswhereCandidates = @()
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vswhereCandidates = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'Common7\IDE\devenv.exe' 2>$null
}

$devenvCandidates = @()
$devenvCandidates += @($runningDevenv)
$devenvCandidates += @($vswhereCandidates)
$devenvCandidates += @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Community\Common7\IDE\devenv.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Professional\Common7\IDE\devenv.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Enterprise\Common7\IDE\devenv.exe"
)

$devenvCandidates = @($devenvCandidates |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -Unique)

if ($devenvCandidates.Count -gt 0) {
    Start-Process -FilePath $devenvCandidates[0] -ArgumentList "`"$resolvedSolution`""
    return
}

Start-Process -FilePath $resolvedSolution
