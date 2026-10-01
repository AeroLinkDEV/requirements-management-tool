@echo off
setlocal
:: DEC-151: stores the Esri World Imagery API key for the flight-management bench out-the-window view, encrypted with
:: Windows DPAPI. The key is typed at a hidden prompt, never passed as an argument. Actions: Set (default), Status, Remove.
:: Windows PowerShell must load its own modules; clearing PSModulePath makes it rebuild its own default.
set "PSModulePath="
title AeroLink - Esri imagery key
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Configure-AeroLinkProtectedImagery.ps1" %*
set "AEROLINK_EXIT=%ERRORLEVEL%"
pause
exit /b %AEROLINK_EXIT%
