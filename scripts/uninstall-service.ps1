<#
.SYNOPSIS
    Removes the IIS Monitor service, program files and Start menu shortcut.

.DESCRIPTION
    History and settings in %ProgramData%\IISMonitor are kept unless -RemoveData is given.
    IIS logging settings changed by "Enable response times" are left as they are.
#>
#Requires -RunAsAdministrator
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'IISMonitor'),
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$serviceName = 'IISMonitor'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $serviceName | Out-Null
    Write-Host 'Service removed.'
}

$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'IIS Monitor.lnk'
Remove-Item $shortcut -ErrorAction SilentlyContinue

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir."
}

if ($RemoveData) {
    Remove-Item (Join-Path $env:ProgramData 'IISMonitor') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Removed settings and history.'
}
