@echo off
setlocal
title AeroLink Remote Demo - Start
cd /d "%~dp0"
echo.
echo ============================================================
echo   AeroLink - Start protected remote demo
echo ============================================================
echo.
echo Starts the local production AeroLink if needed, then starts the
echo policy-backed ngrok tunnel and proves the public endpoint
echo returns 401 before declaring READY.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\AeroLinkRemoteDemo.ps1" -Action Start %*
exit /b %ERRORLEVEL%
