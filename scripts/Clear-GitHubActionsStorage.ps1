param(
    [string]$Repository = 'wertyy111/Vesper-Launcher',
    [string]$Token = $env:GITHUB_TOKEN,
    [int]$KeepNewestRuns = 10,
    [int]$DeleteRunsOlderThanDays = 14,
    [int]$DeleteArtifactsOlderThanDays = 3,
    [int]$DeleteCachesOlderThanDays = 14,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Import-EnvToken {
    param([string]$Path)

    if ($script:Token -or -not (Test-Path -LiteralPath $Path)) {
        return
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*GITHUB_TOKEN\s*=\s*(.+?)\s*$') {
            $script:Token = $Matches[1].Trim().Trim('"').Trim("'")
            return
        }
    }
}

function Invoke-GitHubApi {
    param(
        [ValidateSet('GET', 'DELETE')]
        [string]$Method,
        [string]$Uri
    )

    $headers = @{
        Authorization = "Bearer $Token"
        Accept = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent' = 'VesperLauncherActionsStorageCleanup'
    }

    if ($Method -eq 'DELETE') {
        if ($DryRun) {
            Write-Host "[dry-run] DELETE $Uri"
            return $null
        }

        Write-Host "DELETE $Uri"
    }

    Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
}

function Get-GitHubPages {
    param([string]$Uri)

    $page = 1
    while ($true) {
        $separator = if ($Uri.Contains('?')) { '&' } else { '?' }
        $pageUri = "$Uri${separator}per_page=100&page=$page"
        $result = Invoke-GitHubApi -Method GET -Uri $pageUri

        $items =
            if ($result.workflow_runs) { $result.workflow_runs }
            elseif ($result.artifacts) { $result.artifacts }
            elseif ($result.actions_caches) { $result.actions_caches }
            else { @($result) }

        if (-not $items -or $items.Count -eq 0) {
            break
        }

        foreach ($item in $items) {
            $item
        }

        if ($items.Count -lt 100) {
            break
        }

        $page++
    }
}

Import-EnvToken -Path (Join-Path $PSScriptRoot '..\backend\vesper-account-api\.env')

if (-not $Token) {
    throw 'Set GITHUB_TOKEN or pass -Token with Actions read/write access.'
}

$repoApi = "https://api.github.com/repos/$Repository"
$runsCutoff = (Get-Date).ToUniversalTime().AddDays(-$DeleteRunsOlderThanDays)
$artifactCutoff = (Get-Date).ToUniversalTime().AddDays(-$DeleteArtifactsOlderThanDays)
$cacheCutoff = (Get-Date).ToUniversalTime().AddDays(-$DeleteCachesOlderThanDays)

Write-Host "Cleaning Actions storage for $Repository"
Write-Host "DryRun: $DryRun"

$runs = @(Get-GitHubPages "$repoApi/actions/runs")
$runsToDelete = $runs |
    Sort-Object created_at -Descending |
    Select-Object -Skip $KeepNewestRuns |
    Where-Object { [DateTime]$_.created_at -lt $runsCutoff }

foreach ($run in $runsToDelete) {
    Write-Host ("workflow run: {0} {1} created={2}" -f $run.id, $run.name, $run.created_at)
    Invoke-GitHubApi -Method DELETE -Uri "$repoApi/actions/runs/$($run.id)" | Out-Null
}

$artifacts = @(Get-GitHubPages "$repoApi/actions/artifacts")
$artifactsToDelete = $artifacts |
    Where-Object { -not $_.expired -and [DateTime]$_.created_at -lt $artifactCutoff }

foreach ($artifact in $artifactsToDelete) {
    Write-Host ("artifact: {0} {1} size={2} created={3}" -f $artifact.id, $artifact.name, $artifact.size_in_bytes, $artifact.created_at)
    Invoke-GitHubApi -Method DELETE -Uri "$repoApi/actions/artifacts/$($artifact.id)" | Out-Null
}

$caches = @(Get-GitHubPages "$repoApi/actions/caches")
$cachesToDelete = $caches |
    Where-Object { [DateTime]$_.last_accessed_at -lt $cacheCutoff }

foreach ($cache in $cachesToDelete) {
    Write-Host ("cache: {0} {1} size={2} last_accessed={3}" -f $cache.id, $cache.key, $cache.size_in_bytes, $cache.last_accessed_at)
    Invoke-GitHubApi -Method DELETE -Uri "$repoApi/actions/caches/$($cache.id)" | Out-Null
}

Write-Host ("Deleted candidates: runs={0}, artifacts={1}, caches={2}" -f $runsToDelete.Count, $artifactsToDelete.Count, $cachesToDelete.Count)
