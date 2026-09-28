@echo off
setlocal DisableDelayedExpansion
set "tscmPowerShell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "tscmPowerShell=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
rem Parse the entire final block before running a script that may replace files.
rem The PowerShell coordinator pauses and returns the operation's exit code.
rem This double-click entry point closes its command window; use PS1 in a terminal.
(
    "%tscmPowerShell%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0PackageLauncher.ps1" -Action Install
    exit
)
