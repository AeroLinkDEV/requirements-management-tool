@echo off
setlocal
set "PSModulePath="
cd /d "%~dp0"
call "%~dp0START_AEROLINK_SMTP4DEV.bat"
if not "%ERRORLEVEL%"=="0" exit /b %ERRORLEVEL%
set "Notifications__Smtp__Host=127.0.0.1"
set "Notifications__Smtp__Port=2525"
set "Notifications__Smtp__UseStartTls=false"
set "Notifications__Smtp__From=aerolink@localhost"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Start-AeroLinkProduction.ps1" -NotificationBaseUrl "http://127.0.0.1:5080" %*
exit /b %ERRORLEVEL%
