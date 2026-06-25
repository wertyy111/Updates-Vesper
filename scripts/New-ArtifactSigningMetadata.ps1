[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Endpoint,

    [Parameter(Mandatory = $true)]
    [string]$CodeSigningAccountName,

    [Parameter(Mandatory = $true)]
    [string]$CertificateProfileName,

    [string]$OutputPath = '%LOCALAPPDATA%\VesperLauncher\ArtifactSigning\metadata.json',

    [string[]]$ExcludeCredentials
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    return [System.IO.Path]::GetFullPath($expanded)
}

$outputFullPath = Resolve-NormalizedPath $OutputPath
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$metadata = [ordered]@{
    Endpoint = $Endpoint
    CodeSigningAccountName = $CodeSigningAccountName
    CertificateProfileName = $CertificateProfileName
}

if ($null -ne $ExcludeCredentials -and $ExcludeCredentials.Count -gt 0) {
    $metadata.ExcludeCredentials = @($ExcludeCredentials)
}

$json = $metadata | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText(
    $outputFullPath,
    $json,
    [System.Text.UTF8Encoding]::new($true))

Write-Host "Artifact Signing metadata written to: $outputFullPath"
