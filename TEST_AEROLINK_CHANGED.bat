@echo off
setlocal
:: Windows PowerShell must load its own modules. A PowerShell 7 parent leaves the 7.x module directories
:: first in PSModulePath, and 5.1 then binds Microsoft.PowerShell.Utility from there and loses cmdlets it
:: needs, which surfaces later as an unrelated error deep in a script. Clearing the variable makes
:: PowerShell rebuild its own default, so a launcher behaves the same from Explorer, cmd, or a pwsh prompt.
set "PSModulePath="
cd /d "%~dp0"

rem Windows-friendly changed-area planner. Examples:
rem   TEST_AEROLINK_CHANGED.bat -SinceOriginMain -Explain -DryRun
rem   TEST_AEROLINK_CHANGED.bat -Paths product\client\src\App.tsx -Mode Fast
rem   TEST_AEROLINK_CHANGED.bat -Mode Full

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Get-AeroLinkTestPlan.ps1" %*
set "RESULT=%ERRORLEVEL%"

echo.
if not "%RESULT%"=="0" (
  echo AeroLink changed validation did not complete. Review the error above.
  exit /b %RESULT%
)
echo AeroLink changed validation finished. GitHub Actions full evidence is still required for merge.
exit /b 0
