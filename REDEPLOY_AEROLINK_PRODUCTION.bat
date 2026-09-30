@echo off
setlocal
:: DEC-149: Monday-Friday 08:00-18:00 Eastern, production is redeployed to a moved origin/main only on request, and
:: this is the request. It is recorded, then the installed reconciliation task is started to carry it out.
:: Windows PowerShell must load its own modules. A PowerShell 7 parent leaves the 7.x module directories
:: first in PSModulePath, and 5.1 then binds Microsoft.PowerShell.Utility from there and loses cmdlets it
:: needs. Clearing the variable makes PowerShell rebuild its own default.
set "PSModulePath="
title AeroLink Production - Request Redeploy
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\AeroLinkRemoteDemo.ps1" -Action RequestRedeploy %*
exit /b %ERRORLEVEL%
