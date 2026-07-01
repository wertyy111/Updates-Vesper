[CmdletBinding()]
param()

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

$serviceName = "VesperNetService"
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $existingService) {
    Write-Host "VesperNet Service не установлен."
    exit 0
}

if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
}

sc.exe delete $serviceName | Out-Null
Write-Host "VesperNet Service удалён."
