[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$ServiceExePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    throw "Запусти PowerShell от имени администратора."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ServiceExePath)) {
    $ServiceExePath = Join-Path $repoRoot "windows\VesperNet.Service\bin\$Configuration\net10.0-windows\VesperNet.Service.exe"
}

$resolvedExePath = [System.IO.Path]::GetFullPath($ServiceExePath)
if (-not (Test-Path -LiteralPath $resolvedExePath)) {
    throw "Не найден VesperNet.Service.exe: $resolvedExePath"
}

$serviceName = "VesperNetService"
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    }

    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $serviceName `
    -BinaryPathName ('"{0}"' -f $resolvedExePath) `
    -DisplayName "VesperNet Service" `
    -Description "Локальная служба VesperNet для будущей overlay-сети лаунчера." `
    -StartupType Manual | Out-Null

Start-Service -Name $serviceName
Write-Host "VesperNet Service установлен и запущен."
