<#
.SYNOPSIS
    Builds IIS Monitor into a folder you can copy to an IIS server.

.DESCRIPTION
    Publishes the Windows service and the dashboard as self-contained x64 builds, so the
    server does not need the .NET runtime installed. Output goes to .\publish by default:

        publish\Service\IISMonitor.Service.exe
        publish\Dashboard\IISMonitor.exe
        publish\install-service.cmd (+ .ps1), uninstall-service.cmd (+ .ps1)
        publish\INSTALL.txt, publish\LICENSE

.EXAMPLE
    scripts\publish.cmd      (works even when PowerShell scripts are blocked by execution policy)
.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Output D:\drop\IISMonitor -Runtime win-arm64
.EXAMPLE
    .\scripts\publish.ps1 -Version 1.2.0      (stamps that version into the executables)
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\publish'),
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

dotnet test (Join-Path $root 'tests\IISMonitor.Core.Tests') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$common = @('-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '-p:PublishReadyToRun=true')
if ($Version) { $common += "-p:Version=$Version" }

dotnet publish (Join-Path $root 'src\IISMonitor.Service') @common -o (Join-Path $Output 'Service')
if ($LASTEXITCODE -ne 0) { throw 'Publishing the service failed.' }

dotnet publish (Join-Path $root 'src\IISMonitor.Dashboard') @common -o (Join-Path $Output 'Dashboard')
if ($LASTEXITCODE -ne 0) { throw 'Publishing the dashboard failed.' }

foreach ($script in 'install-service.ps1', 'install-service.cmd', 'uninstall-service.ps1', 'uninstall-service.cmd', 'INSTALL.txt') {
    Copy-Item (Join-Path $PSScriptRoot $script) $Output -Force
}
Copy-Item (Join-Path $root 'LICENSE') $Output -Force

Write-Host ""
Write-Host "Published to $(Resolve-Path $Output)" -ForegroundColor Green
Write-Host "Copy that folder to the IIS server, then right-click install-service.cmd there and choose Run as administrator."
