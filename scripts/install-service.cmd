@echo off
rem Runs install-service.ps1 without changing the PowerShell execution policy on this machine.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-service.ps1" %*
set EXITCODE=%ERRORLEVEL%
pause
exit /b %EXITCODE%
