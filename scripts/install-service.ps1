<#
.SYNOPSIS
    Installs (or upgrades) the IIS Monitor collector service on this server.

.DESCRIPTION
    Copies the published files to Program Files, registers the "IISMonitor" Windows service
    (LocalSystem, automatic start, restart on failure), registers its event log source, starts it,
    and adds a Start menu shortcut for the dashboard. Run from the published folder, as administrator.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -InstallDir 'D:\Tools\IISMonitor'
#>
#Requires -RunAsAdministrator
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'IISMonitor'),
    [string]$SourceDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$serviceName = 'IISMonitor'

foreach ($part in 'Service', 'Dashboard') {
    if (-not (Test-Path (Join-Path $SourceDir $part))) {
        throw "Can't find '$part' next to this script. Run it from the folder created by publish.ps1."
    }
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host 'Stopping the existing service...'
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

Write-Host "Copying files to $InstallDir..."
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
foreach ($part in 'Service', 'Dashboard') {
    $target = Join-Path $InstallDir $part
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path (Join-Path $SourceDir $part) '*') -Destination $target -Recurse -Force
}

$exe = Join-Path $InstallDir 'Service\IISMonitor.Service.exe'
if (-not $existing) {
    Write-Host 'Registering the service...'
    New-Service -Name $serviceName `
        -BinaryPathName "`"$exe`"" `
        -DisplayName 'IIS Monitor collector' `
        -Description 'Collects CPU, memory, disk, network, SQL Server connection and response-time metrics for IIS sites and app pools, and keeps their history.' `
        -StartupType Automatic | Out-Null
} else {
    & sc.exe config $serviceName binPath= "`"$exe`"" start= auto | Out-Null
}

# Restart automatically after a crash: after 10 s, then 30 s, then every minute.
& sc.exe failure $serviceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

if (-not [System.Diagnostics.EventLog]::SourceExists($serviceName)) {
    New-EventLog -LogName Application -Source $serviceName
}

Write-Host 'Starting the service...'
Start-Service -Name $serviceName

$shortcutPath = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'IIS Monitor.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $InstallDir 'Dashboard\IISMonitor.exe'
$shortcut.WorkingDirectory = Join-Path $InstallDir 'Dashboard'
$shortcut.Description = 'IIS Monitor dashboard'
$shortcut.Save()

Write-Host ""
Write-Host 'IIS Monitor is installed and collecting.' -ForegroundColor Green
Write-Host "Open the dashboard from the Start menu ('IIS Monitor') or run $(Join-Path $InstallDir 'Dashboard\IISMonitor.exe')."
Write-Host "Settings and history live in $(Join-Path $env:ProgramData 'IISMonitor')."
