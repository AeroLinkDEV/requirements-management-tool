@echo off
setlocal
set "PSModulePath="
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\AeroLinkSmtp4dev.ps1" -Action Start %*
exit /b %ERRORLEVEL%
