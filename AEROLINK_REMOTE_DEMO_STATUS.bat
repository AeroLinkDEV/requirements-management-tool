@echo off
setlocal
title AeroLink Remote Demo - Status
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\AeroLinkRemoteDemo.ps1" -Action Status %*
exit /b %ERRORLEVEL%
