[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$SetupPath,

    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CertificateThumbprint,
    [switch]$MachineStore,
    [string]$ArtifactSigningMetadataPath,
    [string]$TrustedSigningDlibPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$SignToolPath,

    [Parameter(Mandatory = $true)]
    [string]$GitHubOwner,

    [Parameter(Mandatory = $true)]
    [string]$GitHubRepo,

    [Parameter(Mandatory = $true)]
    [string]$GitHubToken,

    [string]$ReleaseNotesPath,
    [string]$ProductName = 'Vesper Launcher',
    [string]$KeyPath = '%LOCALAPPDATA%\VesperLauncher\NetSparkleKeys',
    [string]$TagName,
    [string]$Branch = 'main',
    [string]$PagesFolder = 'docs',
    [switch]$PreRelease
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    return [System.IO.Path]::GetFullPath($expanded)
}

function Assert-SetupLooksStandalone {
    param([Parameter(Mandatory = $true)][string]$SetupPath)

    $setupItem = Get-Item -LiteralPath $SetupPath
    $companionDllPath = [System.IO.Path]::ChangeExtension($SetupPath, '.dll')

    if (Test-Path -LiteralPath $companionDllPath -PathType Leaf) {
        $companionDllItem = Get-Item -LiteralPath $companionDllPath
        if ($companionDllItem.Length -gt ($setupItem.Length * 10)) {
            throw @(
                "The setup file looks like a framework-dependent apphost, not a standalone installer."
                "Setup: $SetupPath ($($setupItem.Length) bytes)"
                "Companion DLL: $companionDllPath ($($companionDllItem.Length) bytes)"
                "Build the installer with scripts/Build-Installer.ps1 and publish the single-file output."
            ) -join ' '
        }
    }

    if ($setupItem.Length -lt 5MB) {
        throw @(
            "The setup file is suspiciously small: $SetupPath ($($setupItem.Length) bytes)."
            "For this launcher, a valid single-file installer should be much larger because it embeds the payload."
            "Build the installer with scripts/Build-Installer.ps1 and pass that file to Publish-GitHubUpdate.ps1."
        ) -join ' '
    }
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

function Invoke-OptionalSetupSigning {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$FilePath
    )

    $hasCertificatePath = -not [string]::IsNullOrWhiteSpace($CertificatePath)
    $hasThumbprint = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
    $hasArtifactSigning = -not [string]::IsNullOrWhiteSpace($ArtifactSigningMetadataPath)

    if (-not $hasCertificatePath -and -not $hasThumbprint -and -not $hasArtifactSigning) {
        return
    }

    $modesSelected = @($hasCertificatePath, $hasThumbprint, $hasArtifactSigning) | Where-Object { $_ } | Measure-Object | Select-Object -ExpandProperty Count
    if ($modesSelected -gt 1 -and ($hasArtifactSigning -or ($hasCertificatePath -and $hasThumbprint))) {
        throw 'Specify only one signing mode: CertificatePath, CertificateThumbprint, or ArtifactSigningMetadataPath.'
    }

    $signScriptPath = Join-Path $PSScriptRoot 'Sign-Setup.ps1'
    if (-not (Test-Path -LiteralPath $signScriptPath -PathType Leaf)) {
        throw "Signing script not found: $signScriptPath"
    }

    $signParameters = @{
        FilePath     = $FilePath
        TimestampUrl = $TimestampUrl
    }

    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        $signParameters.SignToolPath = $SignToolPath
    }

    if ($hasCertificatePath) {
        $signParameters.CertificatePath = $CertificatePath
        if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
            $signParameters.CertificatePassword = $CertificatePassword
        }
    }
    elseif ($hasThumbprint) {
        $signParameters.CertificateThumbprint = $CertificateThumbprint
        if ($MachineStore) {
            $signParameters.MachineStore = $true
        }
    }
    else {
        $signParameters.ArtifactSigningMetadataPath = $ArtifactSigningMetadataPath
        if (-not [string]::IsNullOrWhiteSpace($TrustedSigningDlibPath)) {
            $signParameters.TrustedSigningDlibPath = $TrustedSigningDlibPath
        }
    }

    Write-Host "Signing staged setup: $FilePath"
    & $signScriptPath @signParameters
    if ($LASTEXITCODE -ne 0) {
        throw "Setup signing failed with exit code $LASTEXITCODE."
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

function Get-HttpStatusCode {
    param([Parameter(Mandatory = $true)][System.Exception]$Exception)

    if ($Exception.PSObject.Properties.Name -contains 'Response' -and $null -ne $Exception.Response) {
        return [int]$Exception.Response.StatusCode
    }

    return $null
}

function Invoke-GitHubJson {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        $Body
    )

    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $script:GitHubHeaders
    }

    $jsonBody = $Body | ConvertTo-Json -Depth 10 -Compress
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $script:GitHubHeaders -ContentType 'application/json' -Body $jsonBody
}

function Try-GetGitHubJson {
    param([Parameter(Mandatory = $true)][string]$Uri)

    try {
        return Invoke-GitHubJson -Method 'GET' -Uri $Uri -Body $null
    }
    catch {
        $statusCode = Get-HttpStatusCode -Exception $_.Exception
        if ($statusCode -eq 404) {
            return $null
        }

        throw
    }
}

function Encode-GitHubContentPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (($Path -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
}

function Get-RepoFileSha {
    param([Parameter(Mandatory = $true)][string]$RepoPath)

    $encodedPath = Encode-GitHubContentPath $RepoPath
    $uri = "https://api.github.com/repos/$GitHubOwner/$GitHubRepo/contents/$($encodedPath)?ref=$([Uri]::EscapeDataString($Branch))"
    $item = Try-GetGitHubJson -Uri $uri

    if ($null -eq $item) {
        return $null
    }

    return $item.sha
}

function Set-RepoFile {
    param(
        [Parameter(Mandatory = $true)][string]$RepoPath,
        [Parameter(Mandatory = $true)][byte[]]$ContentBytes,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $encodedPath = Encode-GitHubContentPath $RepoPath
    $uri = "https://api.github.com/repos/$GitHubOwner/$GitHubRepo/contents/$encodedPath"
    $payload = @{
        message = $Message
        branch  = $Branch
        content = [Convert]::ToBase64String($ContentBytes)
    }

    $sha = Get-RepoFileSha -RepoPath $RepoPath
    if ($null -ne $sha) {
        $payload.sha = $sha
    }

    Invoke-GitHubJson -Method 'PUT' -Uri $uri -Body $payload | Out-Null
}

function Set-RepoTextFile {
    param(
        [Parameter(Mandatory = $true)][string]$RepoPath,
        [AllowEmptyString()]
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Message
    )

    Set-RepoFile -RepoPath $RepoPath -ContentBytes ([System.Text.Encoding]::UTF8.GetBytes($Content)) -Message $Message
}

function Remove-ReleaseAssetIfExists {
    param(
        [Parameter(Mandatory = $true)]$Release,
        [Parameter(Mandatory = $true)][string]$AssetName
    )

    foreach ($asset in @($Release.assets)) {
        if ($asset.name -eq $AssetName) {
            $deleteUri = "https://api.github.com/repos/$GitHubOwner/$GitHubRepo/releases/assets/$($asset.id)"
            Invoke-GitHubJson -Method 'DELETE' -Uri $deleteUri -Body $null | Out-Null
        }
    }
}

function Upload-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)]$Release,
        [Parameter(Mandatory = $true)][string]$AssetPath
    )

    $assetName = Split-Path -Leaf $AssetPath
    $uploadUrl = ($Release.upload_url -replace '\{\?name,label\}$', '') + "?name=$([Uri]::EscapeDataString($assetName))"

    $curlCommand = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -ne $curlCommand) {
        & $curlCommand.Source `
            '--http1.1' `
            '--silent' `
            '--show-error' `
            '--fail' `
            '--location' `
            '--retry' '5' `
            '--retry-all-errors' `
            '-X' 'POST' `
            '-H' "Authorization: Bearer $GitHubToken" `
            '-H' 'Accept: application/vnd.github+json' `
            '-H' 'X-GitHub-Api-Version: 2022-11-28' `
            '-H' 'User-Agent: VesperLauncherUpdatePublisher' `
            '-H' 'Content-Type: application/octet-stream' `
            '--data-binary' "@$AssetPath" `
            $uploadUrl | Out-Null

        if ($LASTEXITCODE -ne 0) {
            throw "curl.exe failed to upload release asset '$assetName' with exit code $LASTEXITCODE."
        }

        return
    }

    Invoke-WebRequest -Method 'POST' -Uri $uploadUrl -Headers $script:GitHubHeaders -ContentType 'application/octet-stream' -InFile $AssetPath | Out-Null
}

$repoRoot = Resolve-NormalizedPath (Join-Path $PSScriptRoot '..')
$setupFullPath = Resolve-NormalizedPath $SetupPath
$keyDirectory = Resolve-NormalizedPath $KeyPath

if (-not (Test-Path $setupFullPath -PathType Leaf)) {
    throw "Setup file not found: $setupFullPath"
}

Assert-SetupLooksStandalone -SetupPath $setupFullPath

if (-not (Test-Path $keyDirectory -PathType Container)) {
    throw "Key directory not found: $keyDirectory. Run scripts/New-NetSparkleKeys.ps1 first."
}

$privateKeyPath = Join-Path $keyDirectory 'NetSparkle_Ed25519.priv'
$publicKeyPath = Join-Path $keyDirectory 'NetSparkle_Ed25519.pub'
if (-not (Test-Path $privateKeyPath -PathType Leaf) -or -not (Test-Path $publicKeyPath -PathType Leaf)) {
    throw "NetSparkle keys are missing in $keyDirectory."
}

$appVersion = $Version.Trim()
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw 'Version must not be empty.'
}

if ([string]::IsNullOrWhiteSpace($TagName)) {
    $TagName = if ($appVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $appVersion
    }
    else {
        "v$appVersion"
    }
}

if ($appVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
    $appVersion = $appVersion.Substring(1)
}

$assetName = Split-Path -Leaf $setupFullPath
$stagingRoot = Join-Path $repoRoot "_temp_build\github-updates\$appVersion"
$binariesDirectory = Join-Path $stagingRoot 'binaries'
$appCastDirectory = Join-Path $stagingRoot 'appcast'
$changeLogDirectory = Join-Path $stagingRoot 'changelog'

New-Item -ItemType Directory -Path $binariesDirectory, $appCastDirectory, $changeLogDirectory -Force | Out-Null

$stagedSetupPath = Join-Path $binariesDirectory $assetName
Copy-Item -Path $setupFullPath -Destination $stagedSetupPath -Force
Invoke-OptionalSetupSigning -RepoRoot $repoRoot -FilePath $stagedSetupPath

$changeLogPath = Join-Path $changeLogDirectory "$appVersion.md"
if ($PSBoundParameters.ContainsKey('ReleaseNotesPath')) {
    $releaseNotesFullPath = Resolve-NormalizedPath $ReleaseNotesPath
    if (-not (Test-Path $releaseNotesFullPath -PathType Leaf)) {
        throw "Release notes file not found: $releaseNotesFullPath"
    }

    Copy-Item -Path $releaseNotesFullPath -Destination $changeLogPath -Force
}
else {
    @(
        "# $ProductName $appVersion"
        ''
        '- Update package published.'
    ) -join [Environment]::NewLine | Set-Content -Path $changeLogPath -Encoding UTF8
}

$downloadBaseUrl = "https://github.com/$GitHubOwner/$GitHubRepo/releases/download/$TagName/"
$changeLogBaseUrl = "https://$GitHubOwner.github.io/$GitHubRepo/changelog/"
$appCastUrl = "https://$GitHubOwner.github.io/$GitHubRepo/appcast.xml"

Ensure-AppCastTool -RepoRoot $repoRoot

Invoke-AppCastTool -RepoRoot $repoRoot -Arguments @(
    '--single-file', $stagedSetupPath,
    '--file-version', $appVersion,
    '--appcast-output-directory', $appCastDirectory,
    '--base-url', $downloadBaseUrl,
    '--change-log-url', $changeLogBaseUrl,
    '--change-log-path', $changeLogDirectory,
    '--product-name', $ProductName,
    '--key-path', $keyDirectory,
    '--human-readable'
)

$appCastPath = Join-Path $appCastDirectory 'appcast.xml'
$appCastSignaturePath = Join-Path $appCastDirectory 'appcast.xml.signature'
if (-not (Test-Path $appCastPath -PathType Leaf) -or -not (Test-Path $appCastSignaturePath -PathType Leaf)) {
    throw "App cast files were not generated in $appCastDirectory."
}

$releaseNotesBody = [System.IO.File]::ReadAllText($changeLogPath)
$indexContent = @(
    '# Vesper Launcher updates'
    ''
    'This repository hosts public launcher update files only.'
    'No source code is published here.'
    ''
    "Latest version: $appVersion"
    "App cast: $appCastUrl"
    "Installer: $downloadBaseUrl$assetName"
) -join [Environment]::NewLine

$script:GitHubHeaders = @{
    Authorization          = "Bearer $GitHubToken"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'VesperLauncherUpdatePublisher'
}

$repoUri = "https://api.github.com/repos/$GitHubOwner/$GitHubRepo"
$null = Invoke-GitHubJson -Method 'GET' -Uri $repoUri -Body $null

$releaseByTagUri = "https://api.github.com/repos/$GitHubOwner/$GitHubRepo/releases/tags/$([Uri]::EscapeDataString($TagName))"
$release = Try-GetGitHubJson -Uri $releaseByTagUri

$releasePayload = @{
    tag_name   = $TagName
    name       = $TagName
    body       = $releaseNotesBody
    draft      = $false
    prerelease = [bool]$PreRelease
}

if ($null -eq $release) {
    $release = Invoke-GitHubJson -Method 'POST' -Uri "https://api.github.com/repos/$GitHubOwner/$GitHubRepo/releases" -Body $releasePayload
}
else {
    $release = Invoke-GitHubJson -Method 'PATCH' -Uri "https://api.github.com/repos/$GitHubOwner/$GitHubRepo/releases/$($release.id)" -Body $releasePayload
}

Remove-ReleaseAssetIfExists -Release $release -AssetName $assetName
Upload-ReleaseAsset -Release $release -AssetPath $stagedSetupPath

$pagesCommitMessage = "Publish launcher update $appVersion"
Set-RepoTextFile -RepoPath "$PagesFolder/index.md" -Content $indexContent -Message $pagesCommitMessage
Set-RepoTextFile -RepoPath "$PagesFolder/.nojekyll" -Content "`n" -Message $pagesCommitMessage
Set-RepoTextFile -RepoPath "$PagesFolder/changelog/$appVersion.md" -Content $releaseNotesBody -Message $pagesCommitMessage
Set-RepoFile -RepoPath "$PagesFolder/appcast.xml" -ContentBytes ([System.IO.File]::ReadAllBytes($appCastPath)) -Message $pagesCommitMessage
Set-RepoFile -RepoPath "$PagesFolder/appcast.xml.signature" -ContentBytes ([System.IO.File]::ReadAllBytes($appCastSignaturePath)) -Message $pagesCommitMessage

$publicKey = (Get-Content -Path $publicKeyPath -Raw).Trim()

Write-Host ''
Write-Host 'GitHub update published.'
Write-Host "Release asset: $downloadBaseUrl$assetName"
Write-Host "AppCast URL:  $appCastUrl"
Write-Host "Public key:   $publicKey"
Write-Host ''
Write-Host 'Make sure the updates repository has GitHub Pages enabled from the selected branch/folder.'
