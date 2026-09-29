@echo off
setlocal DisableDelayedExpansion
rem Double-click for Release; pass PowerShell parameters for command-line use.
set "tscmPowerShell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "tscmPowerShell=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%tscmPowerShell%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-tortoisescm.ps1" %*
set "tscmExit=%ERRORLEVEL%"
echo.
if "%tscmExit%"=="0" (echo Build completed.) else (echo Build failed. See the error above.)
if "%~1"=="" pause
exit /b %tscmExit%
