@echo off
rem Runs publish.ps1 without changing the PowerShell execution policy on this machine.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
set EXITCODE=%ERRORLEVEL%
pause
exit /b %EXITCODE%
