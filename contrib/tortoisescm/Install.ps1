[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$PackageDirectory = $PSScriptRoot,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\TortoiseSCM'),
    [Alias('MachineOverlays')][switch]$EnableMachineOverlays,
    [switch]$NoRegister
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell to install TortoiseSCM.' }
if ($EnableMachineOverlays -and $NoRegister) { throw '-EnableMachineOverlays cannot be combined with -NoRegister.' }
if ($EnableMachineOverlays -and -not (Test-TscmAdministrator)) { throw 'Machine overlays require elevated PowerShell. No files or registration were changed.' }
$installationMutex = Enter-TscmInstallMutex
try {
$package = Assert-TscmPlainPath $PackageDirectory
$manifest = Read-TscmManifest $package -VerifyFiles
$rootPath = Assert-TscmPlainPath $InstallRoot
if ($rootPath -eq [IO.Path]::GetPathRoot($rootPath) -or $rootPath -eq [Environment]::GetFolderPath('UserProfile') -or
    ($rootPath -split '[\\/]') -contains '.plastic') { throw 'Choose a dedicated application installation root.' }
$packageHash = (Get-FileHash -LiteralPath (Join-Path $package 'package-manifest.json') -Algorithm SHA256).Hash
$id = $manifest.version + '-' + $packageHash.Substring(0, 8).ToLowerInvariant() + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$target = Join-TscmOwnedPath $rootPath ('versions\' + $id)
if (-not $PSCmdlet.ShouldProcess($target, 'Copy verified package to a new version directory and register TortoiseSCM')) { return }
if (Test-Path -LiteralPath $target) { throw 'The new version directory already exists. Nothing was overwritten.' }
$copied = New-Object 'Collections.Generic.List[string]'
$snapshot = @(); $registrationAttempted = $false
[IO.Directory]::CreateDirectory($target) | Out-Null
try {
    foreach ($entry in $manifest.files) {
        $source = Join-TscmOwnedPath $package $entry.path
        $destination = Join-TscmOwnedPath $target $entry.path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($source, $destination, $false); $copied.Add($destination)
    }
    $manifestTarget = Join-TscmOwnedPath $target 'package-manifest.json'
    [IO.File]::Copy((Join-Path $package 'package-manifest.json'), $manifestTarget, $false); $copied.Add($manifestTarget)
    Read-TscmManifest $target -VerifyFiles | Out-Null
    $record = [ordered]@{ schemaVersion = 1; product = 'TortoiseSCM'; installRoot = $rootPath; versionDirectory = $target;
        packageManifestSha256 = $packageHash; registered = -not $NoRegister; machineOverlays = [bool]$EnableMachineOverlays; installedUtc = [DateTime]::UtcNow.ToString('o') }
    $recordPath = Join-TscmOwnedPath $target '.tortoisescm-install.json'
    Write-TscmJson $recordPath $record; $copied.Add($recordPath)
    if (-not $NoRegister) {
        $snapshot = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets ([bool]$EnableMachineOverlays)))
        $registrationAttempted = $true
        & (Join-Path $target 'Register-Shell.ps1') -BinaryDirectory $target -EnableMachineOverlays:$EnableMachineOverlays | Write-Verbose
    }
    # Publish the active version only after registration succeeds. Older version files
    # stay intact because Explorer may still have their DLL loaded.
    Write-TscmJson (Join-TscmOwnedPath $rootPath 'current-install.json') $record
    [pscustomobject]@{ version = $manifest.version; versionDirectory = $target; registered = -not $NoRegister; machineOverlays = [bool]$EnableMachineOverlays }
} catch {
    $failure = $_
    if ($registrationAttempted) {
        try {
            if ($EnableMachineOverlays) {
                $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
                try { $ownsStartup = $runKey -and $runKey.GetValue('TortoiseSCMCache') -eq ('"' + (Join-Path $target 'TortoiseSCM.exe') + '" --cache-worker') }
                finally { if ($runKey) { $runKey.Dispose() } }
                if ($ownsStartup) {
                    $stop = Start-Process -FilePath (Join-Path $target 'TortoiseSCM.exe') -ArgumentList '--cache-stop' -WindowStyle Hidden -PassThru
                    if (-not $stop.WaitForExit(10000)) { throw 'New cache worker did not acknowledge stop during rollback.' }
                }
            }
            Restore-TscmRegistrySnapshot $snapshot
            if ($EnableMachineOverlays) {
                $startup = $snapshot | Where-Object { $_.PSObject.Properties['valueName'] -and $_.valueName -eq 'TortoiseSCMCache' }
                if ($startup.exists -and $startup.value -match '^"(?<exe>[^"\r\n]+\\TortoiseSCM\.exe)" --cache-worker$' -and (Test-Path -LiteralPath $Matches.exe -PathType Leaf)) {
                    Start-Process -FilePath $Matches.exe -ArgumentList '--cache-worker' -WindowStyle Hidden | Out-Null
                }
            }
        }
        catch { throw "Installation failed: $failure. Registration rollback also failed: $_. Keep $target for recovery." }
    }
    # Only paths copied by this invocation are removed; never recursively remove an
    # installation root, previous version, user-added file, settings or workspace.
    foreach ($file in $copied) {
        $checked = Assert-TscmPlainPath $file
        if (-not $checked.StartsWith($target.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe failed-install cleanup path.' }
        if (Test-Path -LiteralPath $checked -PathType Leaf) { try { Remove-Item -LiteralPath $checked -Force } catch { Write-Warning "Retained locked file: $checked" } }
    }
    Remove-TscmEmptyDirectories $target @($copied)
    throw $failure
}
} finally { $installationMutex.ReleaseMutex(); $installationMutex.Dispose() }
