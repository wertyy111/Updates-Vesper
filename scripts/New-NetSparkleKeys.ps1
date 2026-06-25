[CmdletBinding()]
param(
    [string]$KeyPath = '%LOCALAPPDATA%\VesperLauncher\NetSparkleKeys',
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    return [System.IO.Path]::GetFullPath($expanded)
}

function Ensure-AppCastTool {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    Push-Location $RepoRoot
    try {
        & dotnet tool restore | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet tool restore failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-AppCastTool {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previousRollForward = $env:DOTNET_ROLL_FORWARD
    $env:DOTNET_ROLL_FORWARD = 'Major'

    Push-Location $RepoRoot
    try {
        & dotnet tool run netsparkle-generate-appcast -- @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "netsparkle-generate-appcast failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location

        if ($null -eq $previousRollForward) {
            Remove-Item Env:DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue
        }
        else {
            $env:DOTNET_ROLL_FORWARD = $previousRollForward
        }
    }
}

$repoRoot = Resolve-NormalizedPath (Join-Path $PSScriptRoot '..')
$keyDirectory = Resolve-NormalizedPath $KeyPath

Ensure-AppCastTool -RepoRoot $repoRoot

if (Test-Path $keyDirectory) {
    if (-not $Force) {
        throw "Key directory already exists: $keyDirectory. Use -Force to replace it."
    }

    Remove-Item -Path $keyDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $keyDirectory -Force | Out-Null

Invoke-AppCastTool -RepoRoot $repoRoot -Arguments @(
    '--generate-keys',
    '--force',
    '--key-path', $keyDirectory
)

$publicKeyPath = Join-Path $keyDirectory 'NetSparkle_Ed25519.pub'
$publicKey = (Get-Content -Path $publicKeyPath -Raw).Trim()

Write-Host ''
Write-Host 'Public key for update-config.json:'
Write-Host $publicKey
Write-Host ''
Write-Host 'Private key directory:'
Write-Host $keyDirectory
Write-Host ''
Write-Host 'Do not publish NetSparkle_Ed25519.priv anywhere public.'
