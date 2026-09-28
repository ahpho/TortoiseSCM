[CmdletBinding()]
param(
    [string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\packages'),
    [string]$Version = ('0.1.0-dev-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$BeyondCompareDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
if ($Version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Use a simple package version containing letters, digits, dot, underscore or hyphen.' }
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$binaries = Assert-TscmPlainPath $BinaryDirectory
$beyondCompareFiles = @()
if (-not [string]::IsNullOrWhiteSpace($BeyondCompareDirectory)) {
    $beyondCompare = Assert-TscmPlainPath $BeyondCompareDirectory
    if (-not (Test-Path -LiteralPath $beyondCompare -PathType Container)) { throw 'BeyondCompareDirectory must be an existing directory.' }
    $required = @('BCompare.exe', 'BComp.exe', 'License.html')
    # Only vendor runtime files are eligible. Never copy personal licenses, sessions,
    # settings, patch utilities, shell extensions or arbitrary subdirectories.
    foreach ($file in ($required + @('BComp.com', '7z.dll', 'BCUnRar.dll', 'mime.types', 'PdfToText.exe', 'XLS_to_TAB_Single.vbs'))) {
        $candidate = Assert-TscmPlainPath (Join-Path $beyondCompare $file)
        if (-not (Test-Path -LiteralPath $candidate)) {
            if ($file -in $required) { throw "Beyond Compare runtime is missing required file: $file" }
            continue
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Beyond Compare runtime entry must be a regular file: $file" }
        $beyondCompareFiles += $candidate
    }
}
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
    foreach ($file in @('Install.cmd', 'Uninstall.cmd', 'PackageLauncher.ps1', 'PackageExplorer.ps1', 'SetupBridge.ps1', 'SetupBeyondCompare.ps1', 'Install.ps1', 'Uninstall.ps1', 'Package.Common.ps1', 'Register-Shell.ps1', 'Unregister-Shell.ps1', 'ModernMenu.Common.ps1', 'Register-ModernShell.ps1', 'Unregister-ModernShell.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ModernMenu') -Destination (Join-Path $stage 'ModernMenu') -Recurse
    & (Join-Path $PSScriptRoot 'Build-ModernMenu.ps1') -OutputDirectory (Join-Path $stage 'ModernMenu') | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'ROADMAP.md') -Destination $stage
    [IO.Directory]::CreateDirectory((Join-Path $stage 'doc')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'doc\TortoiseSCM.md') -Destination (Join-Path $stage 'doc\TortoiseSCM.md')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'doc\TortoiseSCM-parity.md') -Destination (Join-Path $stage 'doc\TortoiseSCM-parity.md')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'doc\TortoiseSCM-validation.md') -Destination (Join-Path $stage 'doc\TortoiseSCM-validation.md')
    if ($beyondCompareFiles.Count -gt 0) {
        $toolDirectory = Join-Path $stage 'Tools\BeyondCompare'
        [IO.Directory]::CreateDirectory($toolDirectory) | Out-Null
        foreach ($file in $beyondCompareFiles) {
            # Recheck immediately before copying; do not follow a substituted link.
            $checkedFile = Assert-TscmPlainPath $file
            Copy-Item -LiteralPath $checkedFile -Destination (Join-Path $toolDirectory ([IO.Path]::GetFileName($checkedFile)))
        }
        $toolInstructions = @'
Beyond Compare runtime is included in Tools/BeyondCompare and detected automatically.
Beyond Compare is a separate third-party product governed by Tools/BeyondCompare/License.html, not the TortoiseSCM GPL license.
Use your own valid Beyond Compare license or its permitted evaluation; three-way text merge requires Pro.
Personal licenses and settings are not included, and the Beyond Compare shell extension is not registered.
'@
    } else {
        $toolInstructions = @'
Comparison and merge tools require a separately installed Beyond Compare 4 or 5; three-way text merge requires Pro.
TortoiseSCM detects BComp.exe automatically or accepts its path in Settings. Beyond Compare is not bundled in this package.
'@
    }
    $readme = @'
TortoiseSCM for Windows x64

Requires Windows x64, .NET Framework 4.8 and an installed Plastic SCM / Unity Version Control client.
{BEYOND_COMPARE_INSTRUCTIONS}
Extract the entire ZIP first. Double-click Install.cmd to install; completion only reports success and does not open TortoiseSCM.
To check out a repository, right-click a folder or its background > TortoiseSCM > Checkout repository.
Double-click Uninstall.cmd to confirm and remove the active installation.
Both launchers keep the result visible; PowerShell script association does not matter.
These double-click entries close their command window when finished; use the PS1 scripts from an existing terminal or automation.
After installation, if Explorer still uses an older extension, wait for all copy, move, delete and extract operations to finish before pressing Enter at the restart confirmation prompt. Restarting Explorer can interrupt unfinished file operations, closes this user's folder windows, and briefly removes the desktop/taskbar; other applications remain running. Type N and press Enter to defer (installation remains complete). You can also restart Windows Explorer later in Task Manager.
Version information: right-click a Plastic workspace > TortoiseSCM > Version information.
It displays the package version, source commit and running program directory.
For advanced use, install from 64-bit PowerShell:
  .\Install.ps1
Optional Explorer status overlays require elevated PowerShell:
  .\Install.ps1 -EnableMachineOverlays
Optional Windows 11 modern menu (unsigned local-test preview) requires elevated Windows PowerShell:
  .\Install.ps1 -EnableModernMenu
Modern menu stays enabled when upgrading an installation that already enabled it.
The preview changes no certificate trust or Developer Mode settings. Signed distribution is outside the current project scope; see ROADMAP.md.
Preview installation without changing files or registry:
  .\Install.ps1 -WhatIf
Portable use without shell registration:
  .\TortoiseSCM.exe --cli --help
Uninstall the current version:
  .\Uninstall.ps1
Remove machine overlays too, from elevated PowerShell:
  .\Uninstall.ps1 -RemoveMachineOverlays

Installations use distinct version directories; an Explorer-loaded DLL is never overwritten.
If uninstall reports locked files, wait for all file operations to finish, restart Explorer and retry; signing out is an alternative. User settings and workspaces are preserved.
package-manifest.json provides SHA-256 integrity checks. This package is not digitally signed.
Source: https://github.com/ahpho/TortoiseSCM
TortoiseSCM license: GPL-2.0-or-later; see LICENSE. See doc/TortoiseSCM.md for usage.
'@
    $readme.Replace('{BEYOND_COMPARE_INSTRUCTIONS}', $toolInstructions) | Set-Content -LiteralPath (Join-Path $stage 'README-PACKAGE.txt') -Encoding UTF8
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $commit = (& git -C $sourceRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $commit = 'unknown' }
    $manifest = [ordered]@{ schemaVersion = 1; product = 'TortoiseSCM'; version = $Version; architecture = 'x64'; sourceCommit = $commit; createdUtc = [DateTime]::UtcNow.ToString('o'); files = $files }
    if ($beyondCompareFiles.Count -gt 0) {
        $manifest.bundledTools = @([ordered]@{ name = 'Beyond Compare'; directory = 'Tools/BeyondCompare'; executable = 'Tools/BeyondCompare/BComp.exe'; license = 'Tools/BeyondCompare/License.html' })
    }
    Write-TscmJson (Join-Path $stage 'package-manifest.json') $manifest
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
