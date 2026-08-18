@echo off
setlocal
:: Windows PowerShell must load its own modules. A PowerShell 7 parent leaves the 7.x module directories
:: first in PSModulePath, and 5.1 then binds Microsoft.PowerShell.Utility from there and loses cmdlets it
:: needs, which surfaces later as an unrelated error deep in a script. Clearing the variable makes
:: PowerShell rebuild its own default, so a launcher behaves the same from Explorer, cmd, or a pwsh prompt.
set "PSModulePath="
title Configure AeroLink Daily Backup
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Configure-AeroLinkBackupSchedule.ps1" %*
if errorlevel 1 (
  echo.
  echo AeroLink backup schedule configuration failed. Review the error above.
  pause
  exit /b 1
)
echo.
echo Use -Action Status to inspect the schedule or -Action Remove to remove it.
pause

