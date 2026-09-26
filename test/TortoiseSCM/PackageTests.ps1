# Portable package/install tests. Actual Explorer registration is never changed.
[CmdletBinding()]
param([string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scripts = Join-Path $repo 'contrib\tortoisescm'
. (Join-Path $scripts 'Package.Common.ps1')
$script:assertions = 0
function Assert([bool]$Condition, [string]$Description) {
    $script:assertions++
    if (-not $Condition) { throw $Description }
    Write-Host "PASS: $Description"
}
function Reject([scriptblock]$Action, [string]$Description) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert $rejected $Description
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('TSCM-package-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$registrationBefore = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets $false)) | ConvertTo-Json -Depth 30
$testRegistryPath = 'Software\TortoiseSCM-PackageTests-' + [Guid]::NewGuid().ToString('N')
try {
    # Every class and machine overlay registered by the installer must be
    # captured before mutation so a failed upgrade can restore it exactly.
    $registrationScript = [IO.File]::ReadAllText((Join-Path $scripts 'Register-Shell.ps1'))
    $classIds = @([regex]::Matches($registrationScript, '\{B1DA45F9-4CD4-4857-A591-96B06953A0[A-F0-9]{2}\}') | ForEach-Object Value | Select-Object -Unique)
    $targets = @(Get-TscmRegistryTargets $true)
    Assert ($classIds.Count -eq 9) 'Registration declares one menu and eight overlay classes'
    foreach ($id in $classIds) {
        Assert (@($targets | Where-Object { $_.hive -eq 'CurrentUser' -and $_.path -eq "Software\Classes\CLSID\$id" }).Count -eq 1) "Installer rollback captures user class $id"
    }
    $overlayNames = @([regex]::Matches($registrationScript, "Name = '(TortoiseSCM [^']+)'") | ForEach-Object { $_.Groups[1].Value })
    foreach ($name in $overlayNames) {
        Assert (@($targets | Where-Object { $_.hive -eq 'LocalMachine' -and $_.path -eq "Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\$name" }).Count -eq 1) "Installer rollback captures machine overlay $name"
    }
    Assert (@($targets | Where-Object { $_.hive -eq 'LocalMachine' -and $_.path -like 'Software\Classes\CLSID\*' }).Count -eq 8) 'Installer rollback captures all machine overlay classes'
    $zip = & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory (Join-Path $fixture 'packages') -Version '0.1.0-package-test'
    Assert (Test-Path -LiteralPath $zip) 'Portable archive is created'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Join-Path $fixture 'unpacked'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $package)
    $manifest = Read-TscmManifest $package -VerifyFiles
    Assert (@($manifest.files | Where-Object { $_.path -match '(?i)(Tests|\.pdb$|\.lib$|\.plastic|^cm\.exe$|settings\.xml)' }).Count -eq 0) 'Package excludes tests, symbols, workspaces and user settings'
    Assert ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -eq ([IO.File]::ReadAllText($zip + '.sha256').Split(' ')[0])) 'Archive SHA-256 sidecar matches'
    $installRoot = Join-Path $fixture 'install root'
    & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot $installRoot -NoRegister -WhatIf | Out-Null
    Assert (-not (Test-Path -LiteralPath $installRoot)) 'Install WhatIf leaves filesystem unchanged'
    $first = & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot $installRoot -NoRegister
    Read-TscmManifest $first.versionDirectory -VerifyFiles | Out-Null
    Assert (-not $first.registered -and (Test-Path -LiteralPath (Join-Path $first.versionDirectory 'TortoiseSCM.exe'))) 'Isolated per-user installation copies verified product files'
    $loadedDll = [IO.File]::Open((Join-Path $first.versionDirectory 'TortoiseSCMShell.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $second = & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot $installRoot -NoRegister }
    finally { $loadedDll.Dispose() }
    Assert ($first.versionDirectory -ne $second.versionDirectory -and (Test-Path -LiteralPath (Join-Path $first.versionDirectory 'TortoiseSCMShell.dll'))) 'Repeated installation uses a new directory and preserves the previous DLL'
    $smoke = New-Object Diagnostics.ProcessStartInfo
    $smoke.FileName = Join-Path $second.versionDirectory 'TortoiseSCM.exe'
    $smoke.Arguments = '--cli --json --help'
    $smoke.UseShellExecute = $false; $smoke.CreateNoWindow = $true; $smoke.RedirectStandardOutput = $true; $smoke.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($smoke)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'Packaged executable smoke test timed out.' }
        $response = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
        Assert ($process.ExitCode -eq 0 -and $response.success -and $stderr.GetAwaiter().GetResult().Length -eq 0) 'Installed portable executable runs its real CLI without a server'
    } finally { $process.Dispose() }
    $userFile = Join-Path $first.versionDirectory 'my-notes.txt'
    [IO.File]::WriteAllText($userFile, 'user owned')
    $userDirectory = Join-Path $first.versionDirectory 'doc\my-empty-directory'
    [IO.Directory]::CreateDirectory($userDirectory) | Out-Null
    & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $first.versionDirectory -NoUnregister -WhatIf | Out-Null
    Assert (Test-Path -LiteralPath (Join-Path $first.versionDirectory 'TortoiseSCM.exe')) 'Uninstall WhatIf preserves product files'
    $removed = & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $first.versionDirectory -NoUnregister
    Assert ($removed.removed -and (Test-Path -LiteralPath $userFile) -and -not (Test-Path -LiteralPath (Join-Path $first.versionDirectory 'TortoiseSCM.exe'))) 'Uninstall removes only package-owned files and preserves user additions'
    Assert (Test-Path -LiteralPath $userDirectory -PathType Container) 'Uninstall preserves unlisted empty user directories'
    $pointer = [IO.File]::ReadAllText((Join-Path $installRoot 'current-install.json')) | ConvertFrom-Json
    Assert ($pointer.versionDirectory -eq $second.versionDirectory) 'Removing an old version preserves the active newer version pointer'
    $doc = Join-Path $second.versionDirectory 'doc\TortoiseSCM.md'
    [IO.File]::AppendAllText($doc, "`nUser edit")
    $partial = & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $second.versionDirectory -NoUnregister -WarningAction SilentlyContinue
    Assert (-not $partial.removed -and (Test-Path -LiteralPath $doc) -and (Test-Path -LiteralPath (Join-Path $second.versionDirectory 'Uninstall.ps1'))) 'Modified package files and retry scripts are preserved'
    [IO.File]::Copy((Join-Path $package 'doc\TortoiseSCM.md'), $doc, $true)
    $completed = & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $second.versionDirectory -NoUnregister
    Assert ($completed.removed -and -not (Test-Path -LiteralPath $second.versionDirectory) -and -not (Test-Path -LiteralPath (Join-Path $installRoot 'current-install.json'))) 'Retry safely completes a partial uninstall'
    $locked = & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot $installRoot -NoRegister
    $lockedPath = Join-Path $locked.versionDirectory 'TortoiseSCMShell.dll'
    $loadedDll = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $pending = & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $locked.versionDirectory -NoUnregister -WarningAction SilentlyContinue
        Assert (-not $pending.removed -and (Test-Path -LiteralPath $lockedPath) -and (Test-Path -LiteralPath (Join-Path $locked.versionDirectory 'package-manifest.json'))) 'Loaded DLL is retained with ownership metadata for a later uninstall'
    } finally { $loadedDll.Dispose() }
    $finished = & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $locked.versionDirectory -NoUnregister
    Assert ($finished.removed -and -not (Test-Path -LiteralPath $locked.versionDirectory)) 'Uninstall completes after the DLL is released'
    Reject { & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $fixture -NoUnregister } 'Uninstall refuses directories outside the version container'
    Reject { Join-TscmOwnedPath $fixture '..\outside.txt' } 'Manifest traversal is rejected'
    Reject { Join-TscmOwnedPath $fixture 'file:stream' } 'Alternate stream manifest paths are rejected'
    [IO.File]::AppendAllText((Join-Path $package 'README-PACKAGE.txt'), 'tampered')
    Reject { & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot (Join-Path $fixture 'tampered-install') -NoRegister } 'Tampered package is rejected before install'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture 'tampered-install'))) 'Package validation failure creates no installation directory'

    $installationMutex = Enter-TscmInstallMutex
    try {
        $contender = Start-Job -ArgumentList $scripts -ScriptBlock {
            param($Scripts)
            . (Join-Path $Scripts 'Package.Common.ps1')
            try {
                $lock = Enter-TscmInstallMutex -TimeoutMilliseconds 100
                $lock.ReleaseMutex(); $lock.Dispose()
                return $false
            } catch { return $_.Exception.Message -like 'Another TortoiseSCM installation*' }
        }
        try {
            if (-not ($contender | Wait-Job -Timeout 20)) { throw 'Concurrent installer lock test timed out.' }
            Assert ([bool]($contender | Receive-Job -ErrorAction Stop)) 'Concurrent processes cannot mutate installation state while its mutex is held'
        } finally { $contender | Stop-Job; $contender | Remove-Job }
    } finally { $installationMutex.ReleaseMutex(); $installationMutex.Dispose() }
    $contender = Start-Job -ArgumentList $scripts -ScriptBlock {
        param($Scripts)
        . (Join-Path $Scripts 'Package.Common.ps1')
        $lock = Enter-TscmInstallMutex -TimeoutMilliseconds 100
        try { return $true } finally { $lock.ReleaseMutex(); $lock.Dispose() }
    }
    try {
        if (-not ($contender | Wait-Job -Timeout 20)) { throw 'Installer lock release test timed out.' }
        Assert ([bool]($contender | Receive-Job -ErrorAction Stop)) 'A subsequent process acquires the released installation mutex'
    } finally { $contender | Stop-Job; $contender | Remove-Job }

    # Exercise exact registration rollback on an unrelated, disposable key only.
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testRegistryPath)
    try { $key.SetValue('original', 'before'); $key.SetValue('RunValue', 'old worker') } finally { $key.Dispose() }
    $snapshot = @(Get-TscmRegistrySnapshot @([pscustomobject]@{ hive = 'CurrentUser'; path = $testRegistryPath }))
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testRegistryPath, $true)
    try { $key.SetValue('original', 'changed'); $key.SetValue('new', 'introduced') } finally { $key.Dispose() }
    Restore-TscmRegistrySnapshot $snapshot
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testRegistryPath)
    try { Assert ($key.GetValue('original') -eq 'before' -and $null -eq $key.GetValue('new')) 'Registration rollback restores original values and removes introduced values' } finally { $key.Dispose() }
    $oneValue = @(Get-TscmRegistrySnapshot @([pscustomobject]@{ hive = 'CurrentUser'; path = $testRegistryPath; valueName = 'RunValue' }))
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testRegistryPath, $true)
    try { $key.SetValue('RunValue', 'new worker'); $key.SetValue('unrelated', 'keep') } finally { $key.Dispose() }
    Restore-TscmRegistrySnapshot $oneValue
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testRegistryPath)
    try { Assert ($key.GetValue('RunValue') -eq 'old worker' -and $key.GetValue('unrelated') -eq 'keep') 'Startup rollback restores only its owned value' } finally { $key.Dispose() }
    $registrationAfter = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets $false)) | ConvertTo-Json -Depth 30
    Assert ($registrationBefore -ceq $registrationAfter) 'Actual Explorer registration remains unchanged throughout package tests'
    Write-Output "PASS: $script:assertions package assertions"
} finally {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testRegistryPath, $false)
    $checked = Assert-TscmPlainPath $fixture
    if (-not $checked.StartsWith([IO.Path]::GetTempPath().TrimEnd('\') + '\TSCM-package-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}
