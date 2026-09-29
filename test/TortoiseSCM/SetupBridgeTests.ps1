# Isolated setup bridge checks. Never register Shell or restart Explorer.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PackageArchive)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scripts = Join-Path $repo 'contrib\tortoisescm'
. (Join-Path $scripts 'Package.Common.ps1')
$artifacts = Join-Path $repo ('bin\TortoiseSCM\qa\setup-bridge-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$root = Join-Path $artifacts 'install space &'
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:assertions = 0; $script:runs = 0
function Assert([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
    $script:assertions++; Write-Host "PASS: $Message"
}
function Run-Bridge([string]$Action, [string]$Archive, [int]$ExpectedCode) {
    $script:runs++
    $result = Join-Path $artifacts ("result-$script:runs.ini")
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $scripts 'SetupBridge.ps1'),
        '-Action', $Action, '-InstallRoot', $root, '-ResultPath', $result, '-NoRegister')
    if ($Archive) { $arguments += @('-PackageArchive', [IO.Path]::GetFullPath($Archive)) }
    $quoted = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $powershell -ArgumentList $quoted -WindowStyle Hidden -PassThru -Wait `
        -RedirectStandardOutput (Join-Path $artifacts ("run-$script:runs.log")) -RedirectStandardError (Join-Path $artifacts ("run-$script:runs-error.log"))
    Assert ($process.ExitCode -eq $ExpectedCode) "Bridge $Action returns expected exit $ExpectedCode (got $($process.ExitCode))"
    $text = [IO.File]::ReadAllText($result)
    Assert ($text.Contains('Success=' + [int]($ExpectedCode -eq 0))) 'Structured result agrees with process success/failure'
    return $text
}
$lock = $null
try {
    Run-Bridge 'Install' $PackageArchive 0 | Out-Null
    $pointerPath = Join-Path $root 'current-install.json'
    $first = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
    Assert (-not $first.registered) 'Test installation has no Shell registration'
    Read-TscmManifest $first.versionDirectory -VerifyFiles | Out-Null
    foreach ($name in @('BComp.exe', 'BCompare.exe', 'License.html')) {
        Assert (Test-Path -LiteralPath (Join-Path $first.versionDirectory ('Tools\BeyondCompare\' + $name))) "Bundled $name exists immediately after install"
    }
    $note = Join-Path $first.versionDirectory 'user-note.txt'
    [IO.File]::WriteAllText($note, 'Keep user files')
    $lock = [IO.File]::Open((Join-Path $first.versionDirectory 'TortoiseSCMShell.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Run-Bridge 'Install' $PackageArchive 0 | Out-Null
    $second = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
    Assert ($first.versionDirectory -ne $second.versionDirectory) 'Reinstall uses a new version directory even while old DLL is loaded'
    Assert ([IO.File]::Exists((Join-Path $first.versionDirectory 'TortoiseSCMShell.dll'))) 'Old loaded DLL was not overwritten'
    Read-TscmManifest $second.versionDirectory -VerifyFiles | Out-Null
    $before = [IO.File]::ReadAllText($pointerPath)
    $badArchive = Join-Path $artifacts 'bad.zip'
    [IO.File]::WriteAllText($badArchive, 'not a zip')
    Run-Bridge 'Install' $badArchive 1 | Out-Null
    Assert ([IO.File]::ReadAllText($pointerPath) -eq $before) 'Corrupt archive leaves the active installation intact'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $altered = Join-Path $artifacts 'altered-package'
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($PackageArchive), $altered)
    [IO.File]::AppendAllText((Join-Path $altered 'TortoiseSCM.exe'), 'tampered')
    $tampered = Join-Path $artifacts 'tampered.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($altered, $tampered)
    Run-Bridge 'Install' $tampered 1 | Out-Null
    Assert ([IO.File]::ReadAllText($pointerPath) -eq $before) 'Manifest hash mismatch leaves the active installation intact'
    $traversal = Join-Path $artifacts 'traversal.zip'
    $zip = [IO.Compression.ZipFile]::Open($traversal, [IO.Compression.ZipArchiveMode]::Create)
    try { $entry = $zip.CreateEntry('../escape.txt'); $writer = New-Object IO.StreamWriter($entry.Open()); try { $writer.Write('bad') } finally { $writer.Dispose() } } finally { $zip.Dispose() }
    Run-Bridge 'Install' $traversal 1 | Out-Null
    Assert ([IO.File]::ReadAllText($pointerPath) -eq $before) 'ZIP traversal is rejected before changing the active installation'
    Run-Bridge 'RestartExplorer' '' 1 | Out-Null
    $blockedResult = Run-Bridge 'Uninstall' '' 2
    Assert ($blockedResult.Contains('TortoiseSCMShell.dll')) 'Blocked uninstall identifies the file to release'
    Assert ([IO.File]::ReadAllText($pointerPath) -eq $before) 'Blocked uninstall preserves the active pointer'
    Read-TscmManifest $first.versionDirectory -VerifyFiles | Out-Null
    Read-TscmManifest $second.versionDirectory -VerifyFiles | Out-Null
    Assert ($true) 'Preflight preserves all files in both current and older versions'
    Assert ([IO.File]::Exists((Join-Path $first.versionDirectory '.tortoisescm-install.json'))) 'Locked old version retains ownership metadata for a retry'
    Assert ([IO.File]::ReadAllText($note) -eq 'Keep user files') 'Partial uninstall preserves user-added files'
    $lock.Dispose(); $lock = $null
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SetupMappedImageTest {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module);
}
'@
    $module = [SetupMappedImageTest]::LoadLibraryEx((Join-Path $first.versionDirectory 'TortoiseSCMShell.dll'), [IntPtr]::Zero, 1)
    Assert ($module -ne [IntPtr]::Zero) 'Test maps an actual shell DLL image like a third-party shell host'
    try {
        $mappedResult = Run-Bridge 'Uninstall' '' 2
        Assert ($mappedResult.Contains('(PID ' + $PID + ')')) 'Preflight identifies the process holding the mapped shell DLL'
        Read-TscmManifest $second.versionDirectory -VerifyFiles | Out-Null
        Assert ([IO.File]::ReadAllText($pointerPath) -eq $before) 'Mapped image contention leaves the active installation intact'
    } finally { [SetupMappedImageTest]::FreeLibrary($module) | Out-Null }
    Run-Bridge 'Uninstall' '' 0 | Out-Null
    Assert (-not [IO.File]::Exists($pointerPath)) 'Successful uninstall removes the active pointer'
    Assert (-not [IO.File]::Exists((Join-Path $first.versionDirectory '.tortoisescm-install.json'))) 'Retry cleans the formerly locked version'
    Assert ([IO.File]::ReadAllText($note) -eq 'Keep user files') 'Successful uninstall preserves user-added files'
    Run-Bridge 'Uninstall' '' 0 | Out-Null
    # Hold a stage file open after Install.ps1 returns to exercise cleanup after
    # the active pointer has already committed. The child exit releases the lock.
    $cleanupFixture = Join-Path $artifacts 'cleanup-package'
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($PackageArchive), $cleanupFixture)
    $fixtureInstall = Join-Path $cleanupFixture 'Install.ps1'
    $stageMarker = Join-Path $artifacts 'locked-stage.txt'
    $injection = @'

$global:setupTestStageLock = [IO.File]::Open((Join-Path $PSScriptRoot 'LICENSE'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
[IO.File]::WriteAllText('__STAGE_MARKER__', $PSScriptRoot)
'@
    [IO.File]::AppendAllText($fixtureInstall, $injection.Replace('__STAGE_MARKER__', $stageMarker.Replace("'", "''")), (New-Object Text.UTF8Encoding($true)))
    $fixtureManifestPath = Join-Path $cleanupFixture 'package-manifest.json'
    $fixtureManifest = [IO.File]::ReadAllText($fixtureManifestPath) | ConvertFrom-Json
    $installEntry = $fixtureManifest.files | Where-Object path -EQ 'Install.ps1'
    $installEntry.sha256 = (Get-FileHash -LiteralPath $fixtureInstall -Algorithm SHA256).Hash.ToLowerInvariant()
    $installEntry.length = (Get-Item -LiteralPath $fixtureInstall).Length
    Write-TscmJson $fixtureManifestPath $fixtureManifest
    $cleanupArchive = Join-Path $artifacts 'cleanup.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($cleanupFixture, $cleanupArchive)
    $cleanupResult = Run-Bridge 'Install' $cleanupArchive 0
    $retainedStage = [IO.File]::ReadAllText($stageMarker)
    Assert ($cleanupResult -match [regex]::Escape($retainedStage)) 'Cleanup warning identifies retained temporary files without changing success'
    $cleanupPointer = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
    Read-TscmManifest $cleanupPointer.versionDirectory -VerifyFiles | Out-Null
    Assert (Test-Path -LiteralPath (Join-Path $retainedStage 'LICENSE')) 'Locked stage file survives cleanup while committed installation remains valid'
    $checkedStage = Assert-TscmPlainPath $retainedStage
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($checkedStage) -ne $tempRoot -or [IO.Path]::GetFileName($checkedStage) -notmatch '^TortoiseSCM-Setup-[a-f0-9]{32}$') { throw 'Unsafe test stage cleanup path.' }
    Remove-Item -LiteralPath $checkedStage -Recurse -Force
    Run-Bridge 'Uninstall' '' 0 | Out-Null
    Write-TscmJson (Join-Path $artifacts 'results.json') ([pscustomobject]@{ assertions = $script:assertions; success = $true; root = $root })
    Write-Output "PASS: $script:assertions setup bridge assertions; artifacts: $artifacts"
} finally { if ($lock) { $lock.Dispose() } }
