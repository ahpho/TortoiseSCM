[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\TortoiseSCM'),
    [string]$VersionDirectory,
    [Alias('MachineOverlays')][switch]$RemoveMachineOverlays,
    [switch]$NoUnregister
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell to uninstall TortoiseSCM.' }
if ($RemoveMachineOverlays -and -not (Test-TscmAdministrator)) { throw 'Machine overlays require elevated PowerShell. No files or registration were changed.' }
$installationMutex = Enter-TscmInstallMutex
try {
$rootPath = Assert-TscmPlainPath $InstallRoot
$pointer = Join-TscmOwnedPath $rootPath 'current-install.json'
if ([string]::IsNullOrWhiteSpace($VersionDirectory)) {
    if (-not (Test-Path -LiteralPath $pointer -PathType Leaf)) { throw 'No current installation record. Supply the exact -VersionDirectory to remove an older version.' }
    $VersionDirectory = ([IO.File]::ReadAllText($pointer) | ConvertFrom-Json).versionDirectory
}
$target = Assert-TscmPlainPath $VersionDirectory
$versions = Join-TscmOwnedPath $rootPath 'versions'
if (-not [IO.Path]::GetDirectoryName($target).Equals($versions, [StringComparison]::OrdinalIgnoreCase)) { throw 'VersionDirectory must be an immediate child of this install root versions directory.' }
$recordPath = Join-TscmOwnedPath $target '.tortoisescm-install.json'
$record = [IO.File]::ReadAllText($recordPath) | ConvertFrom-Json
$manifestPath = Join-TscmOwnedPath $target 'package-manifest.json'
if ($record.schemaVersion -ne 1 -or $record.product -ne 'TortoiseSCM' -or $record.installRoot -ne $rootPath -or $record.versionDirectory -ne $target -or
    $record.packageManifestSha256 -ne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash) { throw 'Installation ownership record or package manifest was changed. Nothing was removed.' }
$manifest = Read-TscmManifest $target
if ($record.registered -and $NoUnregister) { throw 'A registered installation must be unregistered before its binaries can be removed.' }
if ($record.machineOverlays -and -not $RemoveMachineOverlays) { throw 'This installation owns machine overlays. Rerun elevated with -RemoveMachineOverlays so its DLL is not removed while registered.' }
if (-not $PSCmdlet.ShouldProcess($target, 'Unregister this exact version and remove unchanged package-owned files')) { return }
if ($record.registered) {
    # Verify the script before executing it; other modified files are preserved below.
    $unregister = $manifest.files | Where-Object path -eq 'Unregister-Shell.ps1'
    if ((Get-FileHash -LiteralPath (Join-Path $target $unregister.path) -Algorithm SHA256).Hash -ne $unregister.sha256) { throw 'Unregister script was changed. Nothing was removed.' }
    & (Join-Path $target 'Unregister-Shell.ps1') -ExpectedBinaryDirectory $target -RemoveMachineOverlays:$RemoveMachineOverlays | Write-Verbose
}
$retained = New-Object 'Collections.Generic.List[string]'
# Keep installer metadata and scripts when a loaded/modified product file needs a later retry.
$deferred = @('Uninstall.ps1', 'Package.Common.ps1', 'Unregister-Shell.ps1')
foreach ($entry in $manifest.files | Where-Object { $_.path -notin $deferred }) {
    $file = Join-TscmOwnedPath $target $entry.path
    if (-not (Test-Path -LiteralPath $file)) { continue }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
        $retained.Add($file); Write-Warning "Preserved modified package file: $file"; continue
    }
    try { Remove-Item -LiteralPath $file -Force } catch { $retained.Add($file); Write-Warning "File is still in use; sign out and retry uninstall: $file" }
}
if ($retained.Count -eq 0) {
    foreach ($name in $deferred) {
        $file = Join-TscmOwnedPath $target $name
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $entry = $manifest.files | Where-Object path -eq $name
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { $retained.Add($file); Write-Warning "Preserved modified package file: $file" }
    }
}
if ($retained.Count -eq 0) {
    foreach ($name in $deferred) {
        $file = Join-TscmOwnedPath $target $name
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $entry = $manifest.files | Where-Object path -eq $name
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { $retained.Add($file); Write-Warning "Preserved modified package file: $file"; continue }
        try { Remove-Item -LiteralPath $file -Force } catch { $retained.Add($file) }
    }
}
if ($retained.Count -eq 0) { Remove-Item -LiteralPath $manifestPath, $recordPath -Force }
if (Test-Path -LiteralPath $pointer) {
    $current = [IO.File]::ReadAllText($pointer) | ConvertFrom-Json
    if ($current.versionDirectory -eq $target -and $retained.Count -eq 0) { Remove-Item -LiteralPath $pointer -Force }
}
# No recursive deletion: unlisted files belong to the user and remain untouched.
Remove-TscmEmptyDirectories $target @($manifest.files | ForEach-Object { Join-TscmOwnedPath $target $_.path })
[pscustomobject]@{ versionDirectory = $target; removed = $retained.Count -eq 0; retainedFiles = @($retained) }
} finally { $installationMutex.ReleaseMutex(); $installationMutex.Dispose() }
