<#
.SYNOPSIS
    Removes the IIS Monitor service, program files and Start menu shortcut.

.DESCRIPTION
    History and settings in %ProgramData%\IISMonitor are kept unless -RemoveData is given.
    IIS logging settings changed by "Enable response times" or "Set up IIS logging" are left as
    they are. Addresses blocked by the auto blacklist are unblocked: the firewall rules in the
    "IIS Monitor" group and the IIS IP restrictions IIS Monitor added are removed, since nothing
    would expire them once the service is gone.
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

# Blocks made by the auto blacklist.
$rules = @(Get-NetFirewallRule -Group 'IIS Monitor' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'IIS Monitor - blocked addresses #*' })
if ($rules.Count -gt 0) {
    $rules | Remove-NetFirewallRule
    Write-Host "Removed $($rules.Count) IIS Monitor firewall rule(s)."
}

$ledger = Join-Path $env:ProgramData 'IISMonitor\iis-blocks.json'
$appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
if ((Test-Path $ledger) -and (Test-Path $appcmd)) {
    $keys = @(Get-Content $ledger -Raw | ConvertFrom-Json)
    foreach ($key in $keys) {
        $address, $prefix = "$key" -split '/'
        # IIS Monitor adds single addresses: IPv4 with the default mask, IPv6 with "128".
        $entry = if ($address -notmatch ':') { "[ipAddress='$address']" } else { "[ipAddress='$address',subnetMask='$prefix']" }
        & $appcmd set config -section:system.webServer/security/ipSecurity "/-$entry" /commit:apphost | Out-Null
    }
    if ($keys.Count -gt 0) {
        Write-Host "Removed $($keys.Count) IIS IP restriction(s) added by IIS Monitor."
    }
    Remove-Item $ledger -ErrorAction SilentlyContinue
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
