# Prepare a pinned, vendor-signed native runtime and matching source for packaging.
[CmdletBinding()]
param(
    [string]$TortoiseGitDirectory = 'C:\Program Files\TortoiseGit',
    [Parameter(Mandatory = $true)][string]$RedistDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$SourceArchive
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
$vendor = Assert-TscmPlainPath $TortoiseGitDirectory
$redist = Assert-TscmPlainPath $RedistDirectory
$output = Assert-TscmPlainPath $OutputDirectory
if (Test-Path -LiteralPath $output) { throw 'Use a new runtime output directory.' }
foreach ($name in @('TortoiseGitMerge.exe', 'TortoiseGitUDiff.exe')) {
    $file = Join-Path $vendor ('bin\' + $name)
    if ((Get-Item -LiteralPath $file).VersionInfo.FileVersion -ne '2.19.0.0' -or (Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'Valid') {
        throw 'This source profile requires signed official TortoiseGit 2.19.0.0 binaries.'
    }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
foreach ($name in @('TortoiseGitMerge.exe', 'TortoiseGitUDiff.exe', 'gitdll.dll', 'libgit2_tgit.dll', 'zlib1_tgit.dll', 'SciLexer_tgit.dll')) {
    Copy-Item -LiteralPath (Assert-TscmPlainPath (Join-Path $vendor ('bin\' + $name))) -Destination $output
}
Copy-Item -LiteralPath (Join-Path $vendor 'TortoiseGit License.txt') -Destination (Join-Path $output 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $vendor 'apr License.txt') -Destination $output
$crt = Get-ChildItem -LiteralPath $redist -Directory | Where-Object Name -match '^Microsoft\.VC\d+\.CRT$' | Select-Object -First 1
$mfc = Get-ChildItem -LiteralPath $redist -Directory | Where-Object Name -match '^Microsoft\.VC\d+\.MFC$' | Select-Object -First 1
if (-not $crt -or -not $mfc) { throw 'RedistDirectory must contain matching x64 CRT and MFC redistributables.' }
foreach ($name in @('msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'concrt140.dll')) {
    Copy-Item -LiteralPath (Assert-TscmPlainPath (Join-Path $crt.FullName $name)) -Destination $output
}
Copy-Item -LiteralPath (Assert-TscmPlainPath (Join-Path $mfc.FullName 'mfc140u.dll')) -Destination $output
$source = Join-Path $output 'TortoiseGit-source.zip'
if ($SourceArchive) { Copy-Item -LiteralPath (Assert-TscmPlainPath $SourceArchive) -Destination $source }
else {
    # Requires the upstream release and its pinned submodule objects locally. Never changes worktrees.
    & python (Join-Path $PSScriptRoot 'Export-TortoiseToolSources.py') $source
    if ($LASTEXITCODE -ne 0) { throw 'Source export failed; fetch the release and its pinned submodule revisions.' }
}
@'
Unmodified official TortoiseGitMerge and TortoiseGitUDiff 2.19.0.0.
GPLv2: LICENSE.txt. Dependencies retain their own licenses in the source archive.
Complete corresponding source including pinned submodules is retained as a separate build/source artifact;
it is intentionally not included in the end-user TortoiseSCM installer.
Release: REL_2.19.0.0_EXTERNAL, commit 54e40c426abcd38f93cd7f2bbafd9b1206696912.
Extract the source archive and follow build.txt. https://tortoisegit.org/sourcecode/
App-local Microsoft Visual C++ redistributable CRT/MFC DLLs are licensed separately by Microsoft.
'@ | Set-Content -LiteralPath (Join-Path $output 'SOURCE.txt') -Encoding UTF8
Write-Output $output
