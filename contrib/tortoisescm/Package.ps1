[CmdletBinding()]
param(
    [string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\packages'),
    [string]$Version = ('0.1.0-dev-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
if ($Version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Use a simple package version containing letters, digits, dot, underscore or hyphen.' }
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$binaries = Assert-TscmPlainPath $BinaryDirectory
$output = Assert-TscmPlainPath $OutputDirectory
[IO.Directory]::CreateDirectory($output) | Out-Null
$name = "TortoiseSCM-$Version-windows-x64"
$zip = Join-Path $output ($name + '.zip')
if (Test-Path -LiteralPath $zip) { throw "Package already exists: $zip" }
$stage = Join-Path $output ('.stage-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    # Explicit allowlist: test executables, symbols, workspace metadata and cm.exe are never shipped.
    foreach ($file in @('TortoiseSCM.exe', 'TortoiseSCMShell.dll')) { Copy-Item -LiteralPath (Join-Path $binaries $file) -Destination (Join-Path $stage $file) }
    if (Test-Path -LiteralPath (Join-Path $binaries 'TortoiseSCM.exe.config')) { Copy-Item -LiteralPath (Join-Path $binaries 'TortoiseSCM.exe.config') -Destination $stage }
    foreach ($file in @('Install.ps1', 'Uninstall.ps1', 'Package.Common.ps1', 'Register-Shell.ps1', 'Unregister-Shell.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination $stage
    [IO.Directory]::CreateDirectory((Join-Path $stage 'doc')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'doc\TortoiseSCM.md') -Destination (Join-Path $stage 'doc\TortoiseSCM.md')
    @'
TortoiseSCM for Windows x64

Requires Windows x64, .NET Framework 4.8 and an installed Plastic SCM / Unity Version Control client.
Install for this user from 64-bit PowerShell:
  .\Install.ps1
Optional Explorer status overlays require elevated PowerShell:
  .\Install.ps1 -EnableMachineOverlays
Preview installation without changing files or registry:
  .\Install.ps1 -WhatIf
Portable use without shell registration:
  .\TortoiseSCM.exe --cli --help
Uninstall the current version:
  .\Uninstall.ps1
Remove machine overlays too, from elevated PowerShell:
  .\Uninstall.ps1 -RemoveMachineOverlays

Installations use distinct version directories; an Explorer-loaded DLL is never overwritten.
If uninstall reports locked files, sign out and retry. User settings and workspaces are preserved.
package-manifest.json provides SHA-256 integrity checks. This package is not digitally signed.
Source: https://github.com/ahpho/TortoiseSCM
License: GPL-2.0-or-later; see LICENSE. See doc/TortoiseSCM.md for usage.
'@ | Set-Content -LiteralPath (Join-Path $stage 'README-PACKAGE.txt') -Encoding UTF8
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $commit = (& git -C $sourceRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $commit = 'unknown' }
    Write-TscmJson (Join-Path $stage 'package-manifest.json') ([ordered]@{ schemaVersion = 1; product = 'TortoiseSCM'; version = $Version; architecture = 'x64'; sourceCommit = $commit; createdUtc = [DateTime]::UtcNow.ToString('o'); files = $files })
    Read-TscmManifest $stage -VerifyFiles | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
    Write-Output $zip
} finally {
    # The only recursive removal is this unique builder-owned stage, beneath the checked output root.
    $stage = Assert-TscmPlainPath $stage
    if (-not $stage.StartsWith($output.TrimEnd('\') + '\.stage-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package stage cleanup path.' }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
