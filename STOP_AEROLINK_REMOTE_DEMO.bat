@echo off
setlocal
title AeroLink Remote Demo - Stop
cd /d "%~dp0"
echo.
echo ============================================================
echo   AeroLink - Stop protected remote demo
echo ============================================================
echo.
echo Stops only the AeroLink-owned ngrok tunnel, then stops the
echo local AeroLink stack and repository-owned PostgreSQL.
echo Configuration, evidence, database content and credentials
echo are never deleted.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\AeroLinkRemoteDemo.ps1" -Action Stop -IncludeLocalStack %*
exit /b %ERRORLEVEL%
