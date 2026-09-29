@echo off
setlocal DisableDelayedExpansion
rem Run build-tortoisescm.bat first. This creates Setup EXE, ZIP and SHA-256 files.
rem Override runtime locations with TSCM_TORTOISE_TOOLS and TSCM_BEYOND_COMPARE.
set "tscmPowerShell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "tscmPowerShell=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
if not defined TSCM_TORTOISE_TOOLS (
    set "TSCM_TORTOISE_TOOLS=%~dp0bin\TortoiseSCM\native-tools"
    if not exist "%~dp0bin\TortoiseSCM\native-tools\TortoiseGitMerge.exe" if exist "%~dp0bin\TortoiseSCM\native-tools-2.19.0-verified\TortoiseGitMerge.exe" set "TSCM_TORTOISE_TOOLS=%~dp0bin\TortoiseSCM\native-tools-2.19.0-verified"
)
if not defined TSCM_BEYOND_COMPARE set "TSCM_BEYOND_COMPARE=%~dp0..\Tool\BeyondCompare"
if not exist "%TSCM_TORTOISE_TOOLS%\TortoiseGitMerge.exe" goto missingNative
if not exist "%TSCM_BEYOND_COMPARE%\BComp.exe" goto missingBC
"%tscmPowerShell%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0contrib\tortoisescm\Build-Setup.ps1" -TortoiseToolsDirectory "%TSCM_TORTOISE_TOOLS%" -BeyondCompareDirectory "%TSCM_BEYOND_COMPARE%" %*
set "tscmExit=%ERRORLEVEL%"
goto finish
:missingNative
echo ERROR: Prepared Tortoise tools were not found at "%TSCM_TORTOISE_TOOLS%".
echo Run contrib\tortoisescm\Prepare-TortoiseTools.ps1 first, or set TSCM_TORTOISE_TOOLS.
set "tscmExit=1"
goto finish
:missingBC
echo ERROR: Beyond Compare was not found at "%TSCM_BEYOND_COMPARE%".
echo Set TSCM_BEYOND_COMPARE to the runtime directory containing BComp.exe and its license.
set "tscmExit=1"
:finish
echo.
if "%tscmExit%"=="0" (echo Packaging completed. The output path is shown above.) else (echo Packaging failed. See the error above.)
if "%~1"=="" pause
exit /b %tscmExit%
