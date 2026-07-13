@echo off
setlocal
title AeroLink Complete Backup
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Backup-AeroLink.ps1"
if errorlevel 1 (
  echo.
  echo AeroLink backup failed. Review the error above.
  pause
  exit /b 1
)
echo.
echo Backup completed and integrity manifest generated.
pause
