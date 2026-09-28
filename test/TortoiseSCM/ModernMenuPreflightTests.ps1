# GPL-2.0-or-later. Real non-admin installation preflight; no Appx/COM registration.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'contrib/tortoisescm/Package.Common.ps1')
if (Test-TscmAdministrator) { throw 'Run this negative preflight test without elevation.' }
$package = Assert-TscmPlainPath $PackageDirectory
Read-TscmManifest $package -VerifyFiles | Out-Null
$root = Join-Path $repo ('bin/TortoiseSCM/qa/modern-preflight-' + [Guid]::NewGuid().ToString('N'))
$registryBefore = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets $false)) | ConvertTo-Json -Depth 40
$appxBefore = @(Get-AppxPackage -Name TortoiseSCM.ModernMenu.Preview | Select-Object PackageFullName,InstallLocation) | ConvertTo-Json
$rejected = $false
try { & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot $root -EnableModernMenu | Out-Null }
catch {
    if ($_.Exception.Message -notmatch 'elevated') { throw }
    $rejected = $true
    Write-Host ('PASS: Non-admin modern install explains elevation requirement: ' + $_.Exception.Message)
}
if (!$rejected) { throw 'Non-admin preview install unexpectedly succeeded.' }
if (Test-Path -LiteralPath $root) { throw 'Preflight failure created an install root.' }
Write-Host 'PASS: Refused modern install creates no files or active-version pointer'
$registryAfter = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets $false)) | ConvertTo-Json -Depth 40
if ($registryBefore -cne $registryAfter) { throw 'Preflight modified classic registration.' }
Write-Host 'PASS: Existing classic menu and overlay registration unchanged'
$appxAfter = @(Get-AppxPackage -Name TortoiseSCM.ModernMenu.Preview | Select-Object PackageFullName,InstallLocation) | ConvertTo-Json
if ($appxBefore -cne $appxAfter) { throw 'Preflight modified Appx registration.' }
Write-Host 'PASS: Existing modern registration unchanged'
