@echo off
setlocal
:: Windows PowerShell must load its own modules. A PowerShell 7 parent leaves the 7.x module directories
:: first in PSModulePath, and 5.1 then binds Microsoft.PowerShell.Utility from there and loses cmdlets it
:: needs, which surfaces later as an unrelated error deep in a script. Clearing the variable makes
:: PowerShell rebuild its own default, so a launcher behaves the same from Explorer, cmd, or a pwsh prompt.
set "PSModulePath="
title AeroLink Instance - Declaration
cd /d "%~dp0"
echo.
echo ============================================================
echo   AeroLink - Which installation is this?
echo ============================================================
echo.
echo Declares whether this installation is the HOME canonical one,
echo a work-laptop local mirror, or a local demo. The badge in the
echo header reads this, and so do the guards that refuse to replace
echo a canonical database with a snapshot.
echo.
echo Never guessed from the machine name. No database, evidence,
echo attachment or backup is touched either way.
echo.
echo   Status   what this installation currently says it is
echo   Preview  what a declaration would change
echo   Declare  record it
echo.
echo   DECLARE_AEROLINK_INSTANCE.bat Declare WorkLaptopLocal
echo.
set "IA=%~1"
if "%IA%"=="" set "IA=Status"
set "IC=%~2"
set "EXTRA="
if not "%IC%"=="" set "EXTRA=-Classification %IC%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Declare-AeroLinkInstance.ps1" -Action %IA% %EXTRA% %3 %4
exit /b %ERRORLEVEL%
