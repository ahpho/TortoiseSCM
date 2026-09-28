@echo off
setlocal DisableDelayedExpansion
set "tscmPowerShell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "tscmPowerShell=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
rem Parse the entire final block before uninstall can remove this batch file.
rem No further batch-file reads are needed after PowerShell returns.
rem This double-click entry point closes its command window; use PS1 in a terminal.
(
    "%tscmPowerShell%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0PackageLauncher.ps1" -Action Uninstall
    exit
)
