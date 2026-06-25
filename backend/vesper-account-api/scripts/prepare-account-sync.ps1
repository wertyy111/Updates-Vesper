param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,
    [string]$OutputPath = (Join-Path $env:APPDATA "VesperLauncher\account-sync.json"),
    [string]$AuthorizationHeaderName = "",
    [string]$AuthorizationHeaderValue = "",
    [switch]$UpdateProjectTemplate
)

$normalizedBaseUrl = $ApiBaseUrl.Trim()
if ([string]::IsNullOrWhiteSpace($normalizedBaseUrl)) {
    throw "ApiBaseUrl is empty."
}

$normalizedBaseUrl = $normalizedBaseUrl.TrimEnd("/")
if (-not ($normalizedBaseUrl -match "^https?://")) {
    throw "ApiBaseUrl must start with http:// or https://"
}

$config = [ordered]@{
    RegisterUrl = "$normalizedBaseUrl/api/v1/auth/register"
    LoginUrl = "$normalizedBaseUrl/api/v1/auth/login"
    CredentialInfoUrl = "$normalizedBaseUrl/api/v1/auth/credential-info"
    MeUrl = "$normalizedBaseUrl/api/v1/auth/me"
    LogoutUrl = "$normalizedBaseUrl/api/v1/auth/logout"
    AuthorizationHeaderName = $AuthorizationHeaderName
    AuthorizationHeaderValue = $AuthorizationHeaderValue
}

$configJson = $config | ConvertTo-Json -Depth 8
$configDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($configDirectory) -and -not (Test-Path -Path $configDirectory)) {
    New-Item -Path $configDirectory -ItemType Directory | Out-Null
}

Set-Content -Path $OutputPath -Value $configJson -Encoding UTF8
Write-Host "account-sync.json saved to: $OutputPath"

if ($UpdateProjectTemplate) {
    $projectTemplatePath = Join-Path $PSScriptRoot "..\..\..\VesperLauncher\account-sync.json"
    $resolvedProjectTemplatePath = [System.IO.Path]::GetFullPath($projectTemplatePath)
    Set-Content -Path $resolvedProjectTemplatePath -Value $configJson -Encoding UTF8
    Write-Host "Project template updated: $resolvedProjectTemplatePath"
}

