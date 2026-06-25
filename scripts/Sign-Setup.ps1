[CmdletBinding(DefaultParameterSetName = 'pfx')]
param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [Parameter(ParameterSetName = 'pfx', Mandatory = $true)]
    [string]$CertificatePath,

    [Parameter(ParameterSetName = 'pfx')]
    [string]$CertificatePassword,

    [Parameter(ParameterSetName = 'store', Mandatory = $true)]
    [string]$CertificateThumbprint,

    [Parameter(ParameterSetName = 'store')]
    [switch]$MachineStore,

    [Parameter(ParameterSetName = 'artifactSigning', Mandatory = $true)]
    [string]$ArtifactSigningMetadataPath,

    [Parameter(ParameterSetName = 'artifactSigning')]
    [string]$TrustedSigningDlibPath,

    [string]$Description = 'Vesper Launcher Setup',
    [string]$DescriptionUrl = 'https://github.com/wertyy111/Updates-Vesper/releases',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$SignToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Resolve-SignToolPath {
    param([string]$PreferredPath)

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        $expanded = Resolve-NormalizedPath $PreferredPath
        if (Test-Path -LiteralPath $expanded -PathType Leaf) {
            return $expanded
        }

        throw "signtool.exe not found at path: $expanded"
    }

    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        return $command.Source
    }

    $localCandidates = @(
        '%LOCALAPPDATA%\VesperLauncher\ArtifactSigningTools\Microsoft.Windows.SDK.BuildTools*\bin\*\x64\signtool.exe',
        '%LOCALAPPDATA%\VesperLauncher\ArtifactSigningTools\Microsoft.Windows.SDK.BuildTools*\bin\*\signtool.exe'
    )

    foreach ($candidate in $localCandidates) {
        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        if ([string]::IsNullOrWhiteSpace($expanded)) {
            continue
        }

        $match = Get-ChildItem -Path $expanded -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $match) {
            return $match.FullName
        }
    }

    $kitRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Container) }

    foreach ($kitRoot in $kitRoots) {
        $match = Get-ChildItem -LiteralPath $kitRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object {
                $candidate = Join-Path $_.FullName 'x64\signtool.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    return $candidate
                }
            }

        if ($match) {
            return $match
        }
    }

    throw 'signtool.exe was not found. Install the Windows SDK or use Visual Studio Developer PowerShell.'
}

function Resolve-TrustedSigningDlibPath {
    param([string]$PreferredPath)

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        $expanded = Resolve-NormalizedPath $PreferredPath
        if (Test-Path -LiteralPath $expanded -PathType Leaf) {
            return $expanded
        }

        throw "Azure.CodeSigning.Dlib.dll not found at path: $expanded"
    }

    $candidates = @(
        '%LOCALAPPDATA%\VesperLauncher\ArtifactSigningTools\Microsoft.ArtifactSigning.Client*\bin\x64\Azure.CodeSigning.Dlib.dll',
        '%LOCALAPPDATA%\VesperLauncher\ArtifactSigningTools\Microsoft.ArtifactSigning.Client*\Azure.CodeSigning.Dlib.dll',
        '%LOCALAPPDATA%\Microsoft\MicrosoftTrustedSigningClientTools\Azure.CodeSigning.Dlib.dll',
        '%LOCALAPPDATA%\Microsoft\MicrosoftTrustedSigningClientTools\x64\Azure.CodeSigning.Dlib.dll',
        '%ProgramFiles%\Microsoft Trusted Signing Client Tools\Azure.CodeSigning.Dlib.dll',
        '%ProgramFiles%\Microsoft Trusted Signing Client Tools\x64\Azure.CodeSigning.Dlib.dll',
        '%ProgramFiles(x86)%\Microsoft Trusted Signing Client Tools\Azure.CodeSigning.Dlib.dll',
        '%ProgramFiles(x86)%\Microsoft Trusted Signing Client Tools\x64\Azure.CodeSigning.Dlib.dll'
    )

    foreach ($candidate in $candidates) {
        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        if ([string]::IsNullOrWhiteSpace($expanded)) {
            continue
        }

        if ($expanded.Contains('*') -or $expanded.Contains('?')) {
            $match = Get-ChildItem -Path $expanded -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $match) {
                return $match.FullName
            }
        }
        elseif (Test-Path -LiteralPath $expanded -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($expanded)
        }
    }

    $searchRoots = @(
        "$env:LOCALAPPDATA",
        "$env:ProgramFiles",
        "$env:ProgramFiles(x86)"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Container) }

    foreach ($root in $searchRoots) {
        $match = Get-ChildItem -LiteralPath $root -Recurse -Filter 'Azure.CodeSigning.Dlib.dll' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $match) {
            return $match.FullName
        }
    }

    throw 'Azure.CodeSigning.Dlib.dll was not found. Install Microsoft Trusted Signing client tools or pass -TrustedSigningDlibPath.'
}

$fileFullPath = Resolve-NormalizedPath $FilePath
if (-not (Test-Path -LiteralPath $fileFullPath -PathType Leaf)) {
    throw "File to sign was not found: $fileFullPath"
}

$resolvedSignToolPath = Resolve-SignToolPath -PreferredPath $SignToolPath
$arguments = New-Object System.Collections.Generic.List[string]
$arguments.Add('sign')
$arguments.Add('/fd')
$arguments.Add('SHA256')
$arguments.Add('/tr')
$arguments.Add($TimestampUrl)
$arguments.Add('/td')
$arguments.Add('SHA256')
$arguments.Add('/d')
$arguments.Add($Description)
$arguments.Add('/du')
$arguments.Add($DescriptionUrl)

if ($PSCmdlet.ParameterSetName -eq 'pfx') {
    $certificateFullPath = Resolve-NormalizedPath $CertificatePath
    if (-not (Test-Path -LiteralPath $certificateFullPath -PathType Leaf)) {
        throw "Certificate file was not found: $certificateFullPath"
    }

    $arguments.Add('/f')
    $arguments.Add($certificateFullPath)

    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $arguments.Add('/p')
        $arguments.Add($CertificatePassword)
    }
}
elseif ($PSCmdlet.ParameterSetName -eq 'store') {
    $normalizedThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($normalizedThumbprint)) {
        throw 'Certificate thumbprint must not be empty.'
    }

    $arguments.Add('/sha1')
    $arguments.Add($normalizedThumbprint)
    $arguments.Add('/s')
    $arguments.Add('My')

    if ($MachineStore) {
        $arguments.Add('/sm')
    }
}
else {
    $metadataFullPath = Resolve-NormalizedPath $ArtifactSigningMetadataPath
    if (-not (Test-Path -LiteralPath $metadataFullPath -PathType Leaf)) {
        throw "Artifact Signing metadata file was not found: $metadataFullPath"
    }

    $resolvedDlibPath = Resolve-TrustedSigningDlibPath -PreferredPath $TrustedSigningDlibPath
    $arguments.Add('/dlib')
    $arguments.Add($resolvedDlibPath)
    $arguments.Add('/dmdf')
    $arguments.Add($metadataFullPath)
}

$arguments.Add($fileFullPath)

Write-Host "Signing file: $fileFullPath"
Write-Host "Using SignTool: $resolvedSignToolPath"
if ($PSCmdlet.ParameterSetName -eq 'artifactSigning') {
    Write-Host "Using Artifact Signing metadata: $(Resolve-NormalizedPath $ArtifactSigningMetadataPath)"
}

& $resolvedSignToolPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "signtool.exe failed with exit code $LASTEXITCODE."
}

$signature = Get-AuthenticodeSignature -FilePath $fileFullPath
$signature | Select-Object Status, StatusMessage, Path

if ($signature.Status -ne 'Valid') {
    throw "File signature is not valid. Final status: $($signature.Status)"
}

Write-Host 'File signed successfully.'
