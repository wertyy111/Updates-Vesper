param(
    [string]$AccountStatePath = (Join-Path $env:APPDATA "VesperLauncher\account-state.json"),
    [string]$SyncConfigPath = (Join-Path $env:APPDATA "VesperLauncher\account-sync.json"),
    [int]$TimeoutSeconds = 12
)

if (-not (Test-Path -Path $AccountStatePath)) {
    throw "Account state file not found: $AccountStatePath"
}

if (-not (Test-Path -Path $SyncConfigPath)) {
    throw "Sync config file not found: $SyncConfigPath"
}

$account = Get-Content -Raw -Path $AccountStatePath | ConvertFrom-Json
$syncConfig = Get-Content -Raw -Path $SyncConfigPath | ConvertFrom-Json
$registerUrl = if (-not [string]::IsNullOrWhiteSpace($syncConfig.RegisterUrl)) { [string]$syncConfig.RegisterUrl } else { [string]$syncConfig.registerUrl }
$authorizationHeaderName = if (-not [string]::IsNullOrWhiteSpace($syncConfig.AuthorizationHeaderName)) { [string]$syncConfig.AuthorizationHeaderName } else { [string]$syncConfig.authorizationHeaderName }
$authorizationHeaderValue = if (-not [string]::IsNullOrWhiteSpace($syncConfig.AuthorizationHeaderValue)) { [string]$syncConfig.AuthorizationHeaderValue } else { [string]$syncConfig.authorizationHeaderValue }

if ([string]::IsNullOrWhiteSpace($account.Username) -or
    [string]::IsNullOrWhiteSpace($account.PasswordHash) -or
    [string]::IsNullOrWhiteSpace($account.PasswordSalt)) {
    throw "account-state.json does not contain a valid account."
}

if ([string]::IsNullOrWhiteSpace($registerUrl)) {
    throw "RegisterUrl is empty in account-sync.json"
}

$payload = [ordered]@{
    username = $account.Username
    passwordHash = $account.PasswordHash
    passwordSalt = $account.PasswordSalt
    passwordAlgorithm = if ([string]::IsNullOrWhiteSpace($account.PasswordAlgorithm)) { "PBKDF2-SHA256" } else { $account.PasswordAlgorithm }
    passwordIterations = if ($account.PasswordIterations -gt 0) { [int]$account.PasswordIterations } else { 120000 }
    createdAtUtc = if ([string]::IsNullOrWhiteSpace($account.CreatedAtUtc)) { (Get-Date).ToUniversalTime().ToString("o") } else { $account.CreatedAtUtc }
}

$payloadJson = $payload | ConvertTo-Json -Depth 8

$headers = @{
    "Accept" = "application/json"
    "Content-Type" = "application/json"
}

try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor
        [Net.SecurityProtocolType]::Tls12
}
catch {
    # ignore on platforms where this setting is unavailable
}

if (-not [string]::IsNullOrWhiteSpace($authorizationHeaderValue)) {
    $headerName = if ([string]::IsNullOrWhiteSpace($authorizationHeaderName)) { "Authorization" } else { [string]$authorizationHeaderName }
    $headers[$headerName] = [string]$authorizationHeaderValue
}

$statusCode = 0
$responseBody = ""

try {
    $response = Invoke-WebRequest `
        -Uri $registerUrl `
        -Method Post `
        -Headers $headers `
        -Body $payloadJson `
        -TimeoutSec $TimeoutSeconds `
        -UseBasicParsing `
        -ErrorAction Stop
    $statusCode = [int]$response.StatusCode
    $responseBody = [string]$response.Content
}
catch {
    $response = $_.Exception.Response
    if ($null -eq $response) {
        throw
    }

    $statusCode = [int]$response.StatusCode
    try {
        $stream = $response.GetResponseStream()
        if ($null -ne $stream) {
            $reader = New-Object System.IO.StreamReader($stream)
            $responseBody = $reader.ReadToEnd()
            $reader.Dispose()
        }
    }
    catch {
        $responseBody = ""
    }

    if ($statusCode -ne 409) {
        throw "Cloud sync failed. HTTP $statusCode. Body: $responseBody"
    }
}

if (($statusCode -ge 200 -and $statusCode -lt 300) -or $statusCode -eq 409) {
    $account.CloudSyncedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    $updatedJson = $account | ConvertTo-Json -Depth 8
    Set-Content -Path $AccountStatePath -Value $updatedJson -Encoding UTF8
    Write-Host "Account synced successfully. Status: $statusCode"
    exit 0
}

throw "Cloud sync failed. HTTP $statusCode. Body: $responseBody"
