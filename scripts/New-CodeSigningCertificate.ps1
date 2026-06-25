[CmdletBinding()]
param(
    [string]$Subject = 'CN=Vesper Launcher',
    [string]$FriendlyName = 'Vesper Launcher Test Code Signing',
    [int]$YearsValid = 3,
    [string]$OutputDirectory = '%LOCALAPPDATA%\VesperLauncher\CodeSigning',
    [string]$Password,
    [switch]$TrustCurrentUser,
    [switch]$Overwrite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    return [System.IO.Path]::GetFullPath($expanded)
}

function New-RandomPassword {
    param([int]$Length = 24)

    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@$%*-_=+?'
    $bytes = New-Object byte[] ($Length * 2)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    $chars = for ($i = 0; $i -lt $Length; $i++) {
        $alphabet[$bytes[$i] % $alphabet.Length]
    }

    -join $chars
}

$outputRoot = Resolve-NormalizedPath $OutputDirectory
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$safeBaseName = 'vesper-launcher-codesign'
$pfxPath = Join-Path $outputRoot "$safeBaseName.pfx"
$cerPath = Join-Path $outputRoot "$safeBaseName.cer"
$passwordPath = Join-Path $outputRoot "$safeBaseName.password.txt"
$infoPath = Join-Path $outputRoot "$safeBaseName.info.txt"

if (-not $Overwrite) {
    foreach ($path in @($pfxPath, $cerPath, $passwordPath, $infoPath)) {
        if (Test-Path -LiteralPath $path) {
            throw "Certificate output already exists: $path. Use -Overwrite to replace it."
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Password)) {
    $Password = New-RandomPassword
}

$securePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
$notAfter = (Get-Date).AddYears($YearsValid)

$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -FriendlyName $FriendlyName `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm 'SHA256' `
    -KeyExportPolicy Exportable `
    -NotAfter $notAfter

if ($null -eq $certificate) {
    throw 'Failed to create self-signed code signing certificate.'
}

Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $securePassword | Out-Null
Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null

if ($TrustCurrentUser) {
    $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
    $publisherStore = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPublisher', 'CurrentUser')

    try {
        $rootStore.Open('ReadWrite')
        $publisherStore.Open('ReadWrite')
        $rootStore.Add($certificate)
        $publisherStore.Add($certificate)
    }
    finally {
        $rootStore.Close()
        $publisherStore.Close()
    }
}

$info = @(
    "Subject: $($certificate.Subject)"
    "FriendlyName: $($certificate.FriendlyName)"
    "Thumbprint: $($certificate.Thumbprint)"
    "Expires: $($certificate.NotAfter.ToString('u'))"
    "PFX: $pfxPath"
    "CER: $cerPath"
    "TrustedCurrentUser: $TrustCurrentUser"
)

Set-Content -Path $passwordPath -Value $Password -Encoding ASCII
Set-Content -Path $infoPath -Value $info -Encoding UTF8

[pscustomobject]@{
    Subject = $certificate.Subject
    Thumbprint = $certificate.Thumbprint
    ExpiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('u')
    PfxPath = $pfxPath
    CerPath = $cerPath
    PasswordPath = $passwordPath
    InfoPath = $infoPath
    TrustedCurrentUser = [bool]$TrustCurrentUser
}
