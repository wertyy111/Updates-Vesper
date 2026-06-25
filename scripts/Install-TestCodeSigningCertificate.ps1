[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CertificatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    return [System.IO.Path]::GetFullPath($expanded)
}

$certificateFullPath = Resolve-NormalizedPath $CertificatePath
if (-not (Test-Path -LiteralPath $certificateFullPath -PathType Leaf)) {
    throw "Certificate file not found: $certificateFullPath"
}

$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificateFullPath)

$stores = @(
    @{ Name = 'Root'; Location = 'CurrentUser' },
    @{ Name = 'TrustedPublisher'; Location = 'CurrentUser' }
)

foreach ($storeInfo in $stores) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeInfo.Name, $storeInfo.Location)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $existing = $store.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $certificate.Thumbprint,
            $false)

        if ($existing.Count -eq 0) {
            $store.Add($certificate)
        }
    }
    finally {
        $store.Close()
    }
}

[pscustomobject]@{
    Subject = $certificate.Subject
    Thumbprint = $certificate.Thumbprint
    InstalledTo = 'CurrentUser\\Root, CurrentUser\\TrustedPublisher'
}
