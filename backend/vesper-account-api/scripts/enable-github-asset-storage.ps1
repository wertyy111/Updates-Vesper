param(
    [Parameter(Mandatory = $true)]
    [string]$GitHubOwner,

    [Parameter(Mandatory = $true)]
    [string]$GitHubToken,

    [string]$GitHubRepo = "vesper-private-nicknames",
    [string]$GitHubBranch = "main",
    [string]$PublicApiBaseUrl = "",
    [string]$CommitterName = "Vesper Launcher",
    [string]$CommitterEmail = "vesper-launcher@users.noreply.github.com",
    [switch]$SkipMigration,
    [switch]$SkipDeploy
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Get-WranglerTomlPath {
    return Join-Path (Resolve-RepoRoot) "wrangler.toml"
}

function Assert-NotBlank {
    param(
        [string]$Value,
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name is empty."
    }
}

function Update-WranglerVar {
    param(
        [string]$TomlText,
        [string]$Name,
        [string]$Value
    )

    $escapedValue = $Value.Replace("\", "\\").Replace('"', '\"')
    $escapedName = [regex]::Escape($Name)
    $pattern = '(?m)^' + $escapedName + '\s*=\s*".*?"$'
    $replacement = "$Name = ""$escapedValue"""

    if ($TomlText -match $pattern) {
        return [regex]::Replace($TomlText, $pattern, $replacement)
    }

    throw "Variable '$Name' was not found in wrangler.toml."
}

function Invoke-Wrangler {
    param(
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$SecretValue = ""
    )

    $wranglerCommand = "wrangler"
    $npxCommand = (Get-Command "npx.cmd" -ErrorAction Stop).Source
    $fullArguments = @($wranglerCommand) + $Arguments

    if ([string]::IsNullOrWhiteSpace($SecretValue)) {
        & $npxCommand @fullArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Wrangler command failed: $npxCommand $($fullArguments -join ' ')"
        }

        return
    }

    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processInfo.FileName = $npxCommand
    $processInfo.WorkingDirectory = $WorkingDirectory
    $processInfo.Arguments = ($fullArguments -join " ")
    $processInfo.RedirectStandardInput = $true
    $processInfo.RedirectStandardOutput = $false
    $processInfo.RedirectStandardError = $false
    $processInfo.UseShellExecute = $false

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $processInfo
    $null = $process.Start()
    $process.StandardInput.WriteLine($SecretValue)
    $process.StandardInput.Close()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        throw "Wrangler command failed with exit code $($process.ExitCode): $npxCommand $($fullArguments -join ' ')"
    }
}

Assert-NotBlank -Value $GitHubOwner -Name "GitHubOwner"
Assert-NotBlank -Value $GitHubToken -Name "GitHubToken"
Assert-NotBlank -Value $GitHubRepo -Name "GitHubRepo"
Assert-NotBlank -Value $GitHubBranch -Name "GitHubBranch"
Assert-NotBlank -Value $CommitterName -Name "CommitterName"
Assert-NotBlank -Value $CommitterEmail -Name "CommitterEmail"

$repoRoot = Resolve-RepoRoot
$wranglerTomlPath = Get-WranglerTomlPath
$wranglerTomlBackupPath = "$wranglerTomlPath.bak"
$migrationPath = Join-Path $repoRoot "migrations\0004_github_asset_storage.sql"

if (-not (Test-Path -Path $wranglerTomlPath)) {
    throw "wrangler.toml not found: $wranglerTomlPath"
}

if (-not (Test-Path -Path $migrationPath)) {
    throw "migration not found: $migrationPath"
}

$wranglerTomlText = Get-Content -Path $wranglerTomlPath -Raw

if ([string]::IsNullOrWhiteSpace($PublicApiBaseUrl)) {
    if ($wranglerTomlText -match '(?m)^PUBLIC_API_BASE_URL\s*=\s*"(.+?)"$') {
        $PublicApiBaseUrl = $Matches[1]
    }
}

Assert-NotBlank -Value $PublicApiBaseUrl -Name "PublicApiBaseUrl"
$PublicApiBaseUrl = $PublicApiBaseUrl.Trim().TrimEnd("/")

$updatedTomlText = $wranglerTomlText
$updatedTomlText = Update-WranglerVar -TomlText $updatedTomlText -Name "PUBLIC_API_BASE_URL" -Value $PublicApiBaseUrl
$updatedTomlText = Update-WranglerVar -TomlText $updatedTomlText -Name "GITHUB_STORAGE_OWNER" -Value $GitHubOwner
$updatedTomlText = Update-WranglerVar -TomlText $updatedTomlText -Name "GITHUB_STORAGE_REPO" -Value $GitHubRepo
$updatedTomlText = Update-WranglerVar -TomlText $updatedTomlText -Name "GITHUB_STORAGE_BRANCH" -Value $GitHubBranch
$updatedTomlText = Update-WranglerVar -TomlText $updatedTomlText -Name "GITHUB_STORAGE_COMMITTER_NAME" -Value $CommitterName
$updatedTomlText = Update-WranglerVar -TomlText $updatedTomlText -Name "GITHUB_STORAGE_COMMITTER_EMAIL" -Value $CommitterEmail

Copy-Item -Path $wranglerTomlPath -Destination $wranglerTomlBackupPath -Force
Set-Content -Path $wranglerTomlPath -Value $updatedTomlText -Encoding UTF8

Write-Host "wrangler.toml updated:"
Write-Host "  PUBLIC_API_BASE_URL = $PublicApiBaseUrl"
Write-Host "  GITHUB_STORAGE_OWNER = $GitHubOwner"
Write-Host "  GITHUB_STORAGE_REPO = $GitHubRepo"
Write-Host "  GITHUB_STORAGE_BRANCH = $GitHubBranch"

Push-Location $repoRoot
try {
    if (-not $SkipMigration) {
        Write-Host "Applying D1 migration 0004_github_asset_storage.sql..."
        Invoke-Wrangler -WorkingDirectory $repoRoot -Arguments @(
            "d1", "execute", "vesper-account-db",
            "--remote",
            "--file", "migrations/0004_github_asset_storage.sql",
            "--config", "wrangler.toml"
        )
    }

    Write-Host "Uploading GITHUB_STORAGE_TOKEN secret..."
    Invoke-Wrangler -WorkingDirectory $repoRoot -Arguments @(
        "secret", "put", "GITHUB_STORAGE_TOKEN",
        "--config", "wrangler.toml"
    ) -SecretValue $GitHubToken

    if (-not $SkipDeploy) {
        Write-Host "Deploying Cloudflare Worker..."
        Invoke-Wrangler -WorkingDirectory $repoRoot -Arguments @(
            "deploy",
            "--config", "wrangler.toml"
        )
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "GitHub asset storage bootstrap completed."
Write-Host "Health URL: $PublicApiBaseUrl/health"
Write-Host "Backup created: $wranglerTomlBackupPath"
