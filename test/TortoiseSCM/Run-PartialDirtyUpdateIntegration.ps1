# GPL-2.0-or-later. Creates a fresh isolated server branch and retains all evidence.
[CmdletBinding()]
param(
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [string]$ReferenceWorkspace
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$setup = @{ CmPath = $CmPath }
if ($ReferenceWorkspace) { $setup.ReferenceWorkspace = $ReferenceWorkspace }
$manifest = & (Join-Path $PSScriptRoot 'New-TestWorkspace.ps1') @setup
if (-not $manifest -or -not (Test-Path -LiteralPath $manifest)) { throw 'Fixture creation failed.' }
$artifacts = Split-Path -Parent $manifest
$output = Join-Path $artifacts 'PartialDirtyUpdateIntegrationTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'PartialDirtyUpdateIntegrationTests.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /codepage:65001 /langversion:5 /warnaserror /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$output" $sources
if ($LASTEXITCODE -ne 0) { throw 'Integration test compilation failed.' }
$process = Start-Process -FilePath $output -ArgumentList ('"' + $manifest + '" "' + $CmPath + '"') -WindowStyle Hidden -PassThru -Wait `
    -RedirectStandardOutput (Join-Path $artifacts 'console.log') -RedirectStandardError (Join-Path $artifacts 'error.log')
Get-Content -LiteralPath (Join-Path $artifacts 'console.log') -Tail 1
Write-Output "Evidence: $artifacts"
if ($process.ExitCode -ne 0) { throw "Dirty-directory integration failed; inspect $artifacts" }
