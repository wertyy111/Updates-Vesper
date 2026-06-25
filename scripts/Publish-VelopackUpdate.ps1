[CmdletBinding()]
param(
    [string]$Version,
    [string]$ProjectPath = 'VesperLauncher/VesperLauncher.csproj',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Channel = 'win',
    [string]$PackId = 'Vesper.Launcher',
    [string]$PackTitle = 'Vesper Launcher',
    [string]$PackAuthors = 'Vesper Launcher',
    [string]$MainExe = 'VesperLauncher.exe',
    [string]$SetupFileName = 'VesperLauncherSetup.exe',
    [string]$SetupWrapperProjectPath = 'windows/installer-src/Installer.csproj',
    [string]$RepoUrl = 'https://github.com/wertyy111/Updates-Vesper',
    [string]$ReleaseName,
    [string]$TagName,
    [string]$PublicPackageFileName,
    [string]$OutputRoot,
    [string]$ReleaseNotesPath,
    [bool]$KeepOnlyLatest = $true,
    [bool]$PublishStableSetupAsset = $true,
    [bool]$ReplaceExistingRelease = $true,
    [bool]$UseSetupWrapper = $true,
    [switch]$SkipFrontendBuild,
    [switch]$SkipPreviousDownload,
    [switch]$UploadDraft,
    [switch]$Publish,
    [switch]$PreRelease
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

function Get-ProjectVersion {
    param([Parameter(Mandatory = $true)][string]$ProjectFile)

    [xml]$projectXml = Get-Content -LiteralPath $ProjectFile
    $versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
    $projectVersion = if ($null -eq $versionNode) { $null } else { $versionNode.InnerText }
    if ([string]::IsNullOrWhiteSpace($projectVersion)) {
        throw "Version is missing in $ProjectFile."
    }

    return $projectVersion.Trim()
}

function Invoke-RepoDotnet {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Push-Location $RepoRoot
    try {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Ensure-VpkTokenForUpload {
    if (-not [string]::IsNullOrWhiteSpace($env:VPK_TOKEN)) {
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $env:VPK_TOKEN = $env:GITHUB_TOKEN
        return
    }

    throw @(
        'GitHub token is required only for upload/publish.'
        'Set it in the current PowerShell session, not in source code:'
        '$env:VPK_TOKEN = "YOUR_FINE_GRAINED_TOKEN"'
        'Use a token scoped only to the binary updates repository.'
    ) -join [Environment]::NewLine
}

function Rename-SetupPackage {
    param(
        [Parameter(Mandatory = $true)][string]$ReleasesDirectory,
        [Parameter(Mandatory = $true)][string]$SetupFileName
    )

    $safeSetupFileName = [System.IO.Path]::GetFileName($SetupFileName)
    if ([string]::IsNullOrWhiteSpace($safeSetupFileName) -or $safeSetupFileName -ne $SetupFileName) {
        throw 'SetupFileName must be a file name without directory separators.'
    }

    $targetPath = Join-Path $ReleasesDirectory $safeSetupFileName
    $setupCandidates = Get-ChildItem -LiteralPath $ReleasesDirectory -Filter '*-Setup.exe' -File |
        Sort-Object LastWriteTimeUtc -Descending

    $source = $setupCandidates | Where-Object {
        -not [string]::Equals($_.FullName, $targetPath, [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1

    if ($null -eq $source) {
        if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
            Write-Host "Setup package already uses stable name: $safeSetupFileName"
            return
        }

        throw "Velopack setup package was not found in $ReleasesDirectory."
    }

    Copy-Item -LiteralPath $source.FullName -Destination $targetPath -Force
    Write-Host "Stable setup package: $targetPath"
}

function Build-SetupWrapper {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$WrapperProjectPath,
        [Parameter(Mandatory = $true)][string]$InternalSetupPath,
        [Parameter(Mandatory = $true)][string]$StableSetupPath,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$RuntimeIdentifier,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$IntermediateDirectory
    )

    $wrapperProjectFullPath = Resolve-RepoRelativePath -RepoRoot $RepoRoot -Path $WrapperProjectPath
    if (-not (Test-Path -LiteralPath $wrapperProjectFullPath -PathType Leaf)) {
        throw "Setup wrapper project not found: $wrapperProjectFullPath"
    }

    if (-not (Test-Path -LiteralPath $InternalSetupPath -PathType Leaf)) {
        throw "Internal Velopack setup not found: $InternalSetupPath"
    }

    if (Test-Path -LiteralPath $OutputDirectory) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $OutputDirectory, $IntermediateDirectory -Force | Out-Null
    Write-Host "Building Vesper setup wrapper..."

    Invoke-RepoDotnet -RepoRoot $RepoRoot -Arguments @(
        'publish', $wrapperProjectFullPath,
        '-c', $Configuration,
        '-r', $RuntimeIdentifier,
        '--self-contained', 'true',
        '-o', $OutputDirectory,
        '-nologo',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        "-p:VelopackSetup=$InternalSetupPath",
        "-p:BaseIntermediateOutputPath=$IntermediateDirectory/",
        "-p:RestorePackagesPath=$env:NUGET_PACKAGES"
    )

    $publishedWrapperPath = Join-Path $OutputDirectory 'VesperLauncherSetup.exe'
    if (-not (Test-Path -LiteralPath $publishedWrapperPath -PathType Leaf)) {
        throw "Published setup wrapper not found: $publishedWrapperPath"
    }

    Copy-Item -LiteralPath $publishedWrapperPath -Destination $StableSetupPath -Force
    Write-Host "Public setup wrapper: $StableSetupPath"
}

function Get-GitHubRepoPath {
    param([Parameter(Mandatory = $true)][string]$RepoUrl)

    $trimmed = $RepoUrl.Trim().TrimEnd('/')
    if ($trimmed -match 'github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?$') {
        return "$($Matches[1])/$($Matches[2])"
    }

    throw "RepoUrl must point to a GitHub repository: $RepoUrl"
}

function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        $Body = $null
    )

    $headers = @{
        Authorization          = "Bearer $env:VPK_TOKEN"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'VesperLauncherVelopackPublisher'
    }

    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
    }

    return Invoke-RestMethod `
        -Method $Method `
        -Uri $Uri `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body ($Body | ConvertTo-Json -Depth 10 -Compress)
}

function Try-InvokeGitHubApi {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri
    )

    try {
        return Invoke-GitHubApi -Method $Method -Uri $Uri
    }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) {
            return $null
        }

        throw
    }
}

function Upload-GitHubReleaseAsset {
    param(
        [Parameter(Mandatory = $true)]$Release,
        [Parameter(Mandatory = $true)][string]$AssetPath
    )

    $assetName = Split-Path -Leaf $AssetPath
    foreach ($asset in @($Release.assets)) {
        if ($asset.name -eq $assetName) {
            Invoke-GitHubApi -Method 'DELETE' -Uri $asset.url | Out-Null
        }
    }

    $uploadUrl = ($Release.upload_url -replace '\{\?name,label\}$', '') + "?name=$([Uri]::EscapeDataString($assetName))"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -ne $curl) {
        & $curl.Source `
            '--http1.1' `
            '--silent' `
            '--show-error' `
            '--fail' `
            '--location' `
            '--retry' '5' `
            '--retry-all-errors' `
            '-X' 'POST' `
            '-H' "Authorization: Bearer $env:VPK_TOKEN" `
            '-H' 'Accept: application/vnd.github+json' `
            '-H' 'X-GitHub-Api-Version: 2022-11-28' `
            '-H' 'User-Agent: VesperLauncherVelopackPublisher' `
            '-H' 'Content-Type: application/octet-stream' `
            '--data-binary' "@$AssetPath" `
            $uploadUrl | Out-Null

        if ($LASTEXITCODE -ne 0) {
            throw "curl.exe failed to upload release asset '$assetName' with exit code $LASTEXITCODE."
        }

        return
    }

    Invoke-WebRequest `
        -Method 'POST' `
        -Uri $uploadUrl `
        -Headers @{
            Authorization = "Bearer $env:VPK_TOKEN"
            Accept = 'application/vnd.github+json'
            'X-GitHub-Api-Version' = '2022-11-28'
            'User-Agent' = 'VesperLauncherVelopackPublisher'
        } `
        -ContentType 'application/octet-stream' `
        -InFile $AssetPath | Out-Null
}

function Publish-StableSetupAsset {
    param(
        [Parameter(Mandatory = $true)][string]$RepoPath,
        [Parameter(Mandatory = $true)][string]$TagName,
        [Parameter(Mandatory = $true)][string]$StableSetupPath,
        [Parameter(Mandatory = $true)][string]$OriginalSetupName
    )

    $encodedTag = [Uri]::EscapeDataString($TagName)
    $release = Try-InvokeGitHubApi -Method 'GET' -Uri "https://api.github.com/repos/$RepoPath/releases/tags/$encodedTag"
    if ($null -eq $release) {
        throw "GitHub release was not found after upload: $TagName"
    }

    Upload-GitHubReleaseAsset -Release $release -AssetPath $StableSetupPath

    $release = Invoke-GitHubApi -Method 'GET' -Uri "https://api.github.com/repos/$RepoPath/releases/tags/$encodedTag"
    foreach ($asset in @($release.assets)) {
        if ($asset.name -eq $OriginalSetupName) {
            Invoke-GitHubApi -Method 'DELETE' -Uri $asset.url | Out-Null
            Write-Host "Removed internal setup asset from GitHub release: $OriginalSetupName"
        }
    }
}

function Remove-OldGitHubReleases {
    param(
        [Parameter(Mandatory = $true)][string]$RepoPath,
        [Parameter(Mandatory = $true)][string]$CurrentTagName
    )

    $releases = Invoke-GitHubApi -Method 'GET' -Uri "https://api.github.com/repos/$RepoPath/releases?per_page=100"
    foreach ($release in @($releases)) {
        if ($release.tag_name -eq $CurrentTagName) {
            continue
        }

        Write-Host "Deleting old GitHub release: $($release.tag_name)"
        Invoke-GitHubApi -Method 'DELETE' -Uri "https://api.github.com/repos/$RepoPath/releases/$($release.id)" | Out-Null

        $encodedTag = [Uri]::EscapeDataString($release.tag_name)
        try {
            Invoke-GitHubApi -Method 'DELETE' -Uri "https://api.github.com/repos/$RepoPath/git/refs/tags/$encodedTag" | Out-Null
        }
        catch {
            if (-not $_.Exception.Response -or ([int]$_.Exception.Response.StatusCode -ne 404 -and [int]$_.Exception.Response.StatusCode -ne 422)) {
                throw
            }
        }
    }
}

function Remove-GitHubReleaseByTag {
    param(
        [Parameter(Mandatory = $true)][string]$RepoPath,
        [Parameter(Mandatory = $true)][string]$TagName
    )

    $encodedTag = [Uri]::EscapeDataString($TagName)
    $release = Try-InvokeGitHubApi -Method 'GET' -Uri "https://api.github.com/repos/$RepoPath/releases/tags/$encodedTag"
    if ($null -ne $release) {
        Write-Host "Deleting existing GitHub release before replacement: $TagName"
        Invoke-GitHubApi -Method 'DELETE' -Uri "https://api.github.com/repos/$RepoPath/releases/$($release.id)" | Out-Null
    }

    try {
        Invoke-GitHubApi -Method 'DELETE' -Uri "https://api.github.com/repos/$RepoPath/git/refs/tags/$encodedTag" | Out-Null
    }
    catch {
        if (-not $_.Exception.Response -or ([int]$_.Exception.Response.StatusCode -ne 404 -and [int]$_.Exception.Response.StatusCode -ne 422)) {
            throw
        }
    }
}

function Rename-PublicPackageAsset {
    param(
        [Parameter(Mandatory = $true)][string]$RepoPath,
        [Parameter(Mandatory = $true)][string]$TagName,
        [Parameter(Mandatory = $true)][string]$ReleasesDirectory,
        [Parameter(Mandatory = $true)][string]$OldPackageFileName,
        [Parameter(Mandatory = $true)][string]$NewPackageFileName
    )

    $safePackageFileName = [System.IO.Path]::GetFileName($NewPackageFileName)
    if ([string]::IsNullOrWhiteSpace($safePackageFileName) -or $safePackageFileName -ne $NewPackageFileName) {
        throw 'PublicPackageFileName must be a file name without directory separators.'
    }

    if (-not $safePackageFileName.EndsWith('.nupkg', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'PublicPackageFileName must end with .nupkg.'
    }

    $encodedTag = [Uri]::EscapeDataString($TagName)
    $release = Invoke-GitHubApi -Method 'GET' -Uri "https://api.github.com/repos/$RepoPath/releases/tags/$encodedTag"
    $asset = @($release.assets) | Where-Object { $_.name -eq $OldPackageFileName } | Select-Object -First 1
    if ($null -eq $asset) {
        Write-Host "Package asset already renamed or missing: $OldPackageFileName"
    }
    else {
        Invoke-GitHubApi -Method 'PATCH' -Uri $asset.url -Body @{ name = $safePackageFileName } | Out-Null
        Write-Host "Renamed public package asset: $safePackageFileName"
    }

    $releaseIndexPath = Join-Path $ReleasesDirectory 'releases.win.json'
    $legacyIndexPath = Join-Path $ReleasesDirectory 'RELEASES'
    foreach ($indexPath in @($releaseIndexPath, $legacyIndexPath)) {
        if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
            throw "Velopack index file not found: $indexPath"
        }

        $content = [System.IO.File]::ReadAllText($indexPath)
        [System.IO.File]::WriteAllText($indexPath, $content.Replace($OldPackageFileName, $safePackageFileName))
    }

    $release = Invoke-GitHubApi -Method 'GET' -Uri "https://api.github.com/repos/$RepoPath/releases/tags/$encodedTag"
    Upload-GitHubReleaseAsset -Release $release -AssetPath $releaseIndexPath
    $release = Invoke-GitHubApi -Method 'GET' -Uri "https://api.github.com/repos/$RepoPath/releases/tags/$encodedTag"
    Upload-GitHubReleaseAsset -Release $release -AssetPath $legacyIndexPath
}

$repoRoot = Resolve-NormalizedPath (Join-Path $PSScriptRoot '..')
$projectFullPath = Resolve-NormalizedPath (Join-Path $repoRoot $ProjectPath)
if (-not (Test-Path -LiteralPath $projectFullPath -PathType Leaf)) {
    throw "Project not found: $projectFullPath"
}

$userBuildRoot = Join-Path $repoRoot '_build_verify/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $userBuildRoot 'dotnet_home'
$env:NUGET_PACKAGES = Join-Path $userBuildRoot 'nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion -ProjectFile $projectFullPath
}

$safeVersion = $Version.Trim().TrimStart('v')
if ([string]::IsNullOrWhiteSpace($safeVersion)) {
    throw 'Version must not be empty.'
}

$effectiveOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    '_build_verify/velopack'
}
else {
    $OutputRoot
}

$outputRootFullPath = Resolve-RepoRelativePath -RepoRoot $repoRoot -Path $effectiveOutputRoot
$releaseRoot = Join-Path $outputRootFullPath $safeVersion
$publishDirectory = Join-Path $releaseRoot "publish-$RuntimeIdentifier"
$releasesDirectory = Join-Path $releaseRoot 'Releases'
$buildTempDirectory = Join-Path $releaseRoot 'temp'
$buildIntermediateDirectory = Join-Path $releaseRoot 'obj'
$setupWrapperPublishDirectory = Join-Path $releaseRoot 'setup-wrapper-publish'
$setupWrapperIntermediateDirectory = Join-Path $releaseRoot 'setup-wrapper-obj'
$iconPath = Resolve-NormalizedPath (Join-Path $repoRoot 'VesperLauncher/Assets/vesper-app.ico')

if ($SkipPreviousDownload -and (Test-Path -LiteralPath $releasesDirectory)) {
    $resolvedReleasesDirectory = Resolve-NormalizedPath $releasesDirectory
    $resolvedOutputRoot = Resolve-NormalizedPath $outputRootFullPath
    if (-not $resolvedReleasesDirectory.StartsWith($resolvedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected Velopack releases directory: $resolvedReleasesDirectory"
    }

    Remove-Item -LiteralPath $resolvedReleasesDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $releaseRoot, $publishDirectory, $releasesDirectory, $buildTempDirectory, $buildIntermediateDirectory -Force | Out-Null

$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$env:TEMP = $buildTempDirectory
$env:TMP = $buildTempDirectory

Write-Host "Publishing Vesper Launcher $safeVersion for $RuntimeIdentifier..."
$publishArguments = @(
    'publish', $projectFullPath,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-o', $publishDirectory,
    '-nologo',
    '-p:PublishSingleFile=false',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-p:SkipLockedLauncherCheck=true',
    "-p:BaseIntermediateOutputPath=$buildIntermediateDirectory/",
    "-p:RestorePackagesPath=$env:NUGET_PACKAGES",
    "-p:SkipFrontendBuild=$($SkipFrontendBuild.IsPresent.ToString().ToLowerInvariant())"
)
Invoke-RepoDotnet -RepoRoot $repoRoot -Arguments $publishArguments

Write-Host "Restoring Velopack CLI..."
Invoke-RepoDotnet -RepoRoot $repoRoot -Arguments @('tool', 'restore')

if (-not $SkipPreviousDownload) {
    Write-Host "Trying to download previous Velopack release for delta generation..."
    try {
        Invoke-RepoDotnet -RepoRoot $repoRoot -Arguments @(
            'tool', 'run', 'vpk', '--',
            'download', 'github',
            '--outputDir', $releasesDirectory,
            '--channel', $Channel,
            '--repoUrl', $RepoUrl
        )
    }
    catch {
        Write-Warning "Previous release download was skipped: $($_.Exception.Message)"
    }
}

Write-Host "Packing Velopack release..."
$packArguments = @(
    'tool', 'run', 'vpk', '--',
    'pack',
    '--packId', $PackId,
    '--packVersion', $safeVersion,
    '--packDir', $publishDirectory,
    '--mainExe', $MainExe,
    '--outputDir', $releasesDirectory,
    '--channel', $Channel,
    '--runtime', $RuntimeIdentifier,
    '--packTitle', $PackTitle,
    '--packAuthors', $PackAuthors,
    '--icon', $iconPath
)

if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $releaseNotesFullPath = Resolve-NormalizedPath $ReleaseNotesPath
    if (-not (Test-Path -LiteralPath $releaseNotesFullPath -PathType Leaf)) {
        throw "Release notes file not found: $releaseNotesFullPath"
    }

    $packArguments += @('--releaseNotes', $releaseNotesFullPath)
}

Invoke-RepoDotnet -RepoRoot $repoRoot -Arguments $packArguments
Rename-SetupPackage -ReleasesDirectory $releasesDirectory -SetupFileName $SetupFileName
$stableSetupPath = Join-Path $releasesDirectory $SetupFileName
$internalSetupPath = Join-Path $releasesDirectory "$PackId-$Channel-Setup.exe"

if ($UseSetupWrapper) {
    Build-SetupWrapper `
        -RepoRoot $repoRoot `
        -WrapperProjectPath $SetupWrapperProjectPath `
        -InternalSetupPath $internalSetupPath `
        -StableSetupPath $stableSetupPath `
        -Configuration $Configuration `
        -RuntimeIdentifier $RuntimeIdentifier `
        -OutputDirectory $setupWrapperPublishDirectory `
        -IntermediateDirectory $setupWrapperIntermediateDirectory
}

if ($Publish -or $UploadDraft) {
    Ensure-VpkTokenForUpload

    $publishValue = if ($Publish) { 'true' } else { 'false' }
    $effectiveTagName = if ([string]::IsNullOrWhiteSpace($TagName)) {
        "v$safeVersion"
    }
    else {
        $TagName.Trim()
    }
    $releaseNameValue = if ([string]::IsNullOrWhiteSpace($ReleaseName)) {
        "Vesper Launcher $safeVersion"
    }
    else {
        $ReleaseName.Trim()
    }

    $repoPath = Get-GitHubRepoPath -RepoUrl $RepoUrl
    if ($ReplaceExistingRelease) {
        Remove-GitHubReleaseByTag -RepoPath $repoPath -TagName $effectiveTagName
    }

    Write-Host "Uploading Velopack release to $RepoUrl (publish=$publishValue)..."
    $uploadArguments = @(
        'tool', 'run', 'vpk', '--',
        'upload', 'github',
        '--outputDir', $releasesDirectory,
        '--channel', $Channel,
        '--repoUrl', $RepoUrl,
        '--publish', $publishValue,
        '--releaseName', $releaseNameValue
    )

    $uploadArguments += @('--tag', $effectiveTagName)

    if ($PreRelease) {
        $uploadArguments += @('--pre', 'true')
    }

    Invoke-RepoDotnet -RepoRoot $repoRoot -Arguments $uploadArguments

    $internalSetupName = "$PackId-$Channel-Setup.exe"

    if ($PublishStableSetupAsset -and (Test-Path -LiteralPath $stableSetupPath -PathType Leaf)) {
        Publish-StableSetupAsset `
            -RepoPath $repoPath `
            -TagName $effectiveTagName `
            -StableSetupPath $stableSetupPath `
            -OriginalSetupName $internalSetupName
    }

    if (-not [string]::IsNullOrWhiteSpace($PublicPackageFileName)) {
        Rename-PublicPackageAsset `
            -RepoPath $repoPath `
            -TagName $effectiveTagName `
            -ReleasesDirectory $releasesDirectory `
            -OldPackageFileName "$PackId-$safeVersion-full.nupkg" `
            -NewPackageFileName $PublicPackageFileName.Trim()
    }

    if ($Publish -and $KeepOnlyLatest) {
        Remove-OldGitHubReleases -RepoPath $repoPath -CurrentTagName $effectiveTagName
    }
}
else {
    Write-Host ''
    Write-Host 'Velopack packages were created locally only.'
    Write-Host 'Nothing was uploaded, so users will not receive this update yet.'
    Write-Host 'Use -UploadDraft for a GitHub draft or -Publish when you explicitly want users to update.'
}

Write-Host ''
Write-Host "Release output: $releasesDirectory"

if ($null -eq $previousTemp) {
    Remove-Item Env:TEMP -ErrorAction SilentlyContinue
}
else {
    $env:TEMP = $previousTemp
}

if ($null -eq $previousTmp) {
    Remove-Item Env:TMP -ErrorAction SilentlyContinue
}
else {
    $env:TMP = $previousTmp
}

