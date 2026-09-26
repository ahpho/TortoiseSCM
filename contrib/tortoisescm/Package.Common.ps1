# Shared package validation and narrowly scoped installation helpers. GPL-2.0-or-later.
Set-StrictMode -Version Latest

function Assert-TscmPlainPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    for ($current = $absolute; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Links and junctions are not supported for package paths: $current"
            }
        }
        if ([IO.Path]::GetPathRoot($current) -eq $current) { break }
    }
    return $absolute
}

function Join-TscmOwnedPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative -match '[:*?"<>|]' -or $Relative -match '[\x00-\x1f]' -or
        @($Relative -split '[\\/]' | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count) {
        throw "Invalid package-relative path: $Relative"
    }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $candidate = Assert-TscmPlainPath (Join-Path $rootPath $Relative)
    if (-not $candidate.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escaped its root.' }
    return $candidate
}

function Read-TscmManifest([string]$Directory, [switch]$VerifyFiles) {
    $rootPath = Assert-TscmPlainPath $Directory
    $manifestPath = Join-TscmOwnedPath $rootPath 'package-manifest.json'
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'TortoiseSCM' -or $manifest.architecture -ne 'x64' -or
        $manifest.version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$' -or @($manifest.files).Count -lt 7) { throw 'Invalid TortoiseSCM package manifest.' }
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        $target = Join-TscmOwnedPath $rootPath ([string]$file.path)
        if (-not $seen.Add([string]$file.path) -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$file.length -lt 0) { throw 'Invalid or duplicate manifest entry.' }
        if ($file.path -in @('package-manifest.json', '.tortoisescm-install.json', 'current-install.json')) { throw 'Manifest includes reserved installation metadata.' }
        if ($VerifyFiles) {
            if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target).Length -ne [long]$file.length -or
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw "Package hash or length mismatch: $($file.path)" }
        }
    }
    foreach ($required in @('TortoiseSCM.exe', 'TortoiseSCMShell.dll', 'Install.ps1', 'Uninstall.ps1', 'Package.Common.ps1', 'Register-Shell.ps1', 'Unregister-Shell.ps1', 'LICENSE')) {
        if (-not $seen.Contains($required)) { throw "Package is missing $required" }
    }
    return $manifest
}

function Write-TscmJson([string]$Path, $Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 15), (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temporary, $Path) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Test-TscmAdministrator {
    return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Enter-TscmInstallMutex([int]$TimeoutMilliseconds = 30000) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mutex = New-Object Threading.Mutex($false, ('Global\TortoiseSCM.Install.' + $sid))
    try {
        try { $acquired = $mutex.WaitOne($TimeoutMilliseconds) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Another TortoiseSCM installation or removal is in progress. Retry after it finishes.' }
        return $mutex
    } catch { $mutex.Dispose(); throw }
}

function Remove-TscmEmptyDirectories([string]$Root, [string[]]$OwnedFiles) {
    $rootPath = Assert-TscmPlainPath $Root
    $directories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $OwnedFiles) {
        $path = Assert-TscmPlainPath $file
        if (-not $path.StartsWith($rootPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe directory cleanup path.' }
        for ($parent = [IO.Path]::GetDirectoryName($path); $parent -ne $rootPath; $parent = [IO.Path]::GetDirectoryName($parent)) {
            [void]$directories.Add($parent)
        }
    }
    foreach ($path in @($directories | Sort-Object Length -Descending)) {
        if ((Test-Path -LiteralPath $path -PathType Container) -and @(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) { Remove-Item -LiteralPath $path }
    }
    if ((Test-Path -LiteralPath $rootPath -PathType Container) -and @(Get-ChildItem -LiteralPath $rootPath -Force).Count -eq 0) { Remove-Item -LiteralPath $rootPath }
}

function Get-TscmRegistryTargets([bool]$MachineOverlays) {
    $classes = @('D2', 'D3', 'D4', 'D5', 'D6', 'D7', 'D8', 'D9', 'DA') | ForEach-Object { '{B1DA45F9-4CD4-4857-A591-96B06953A0' + $_ + '}' }
    foreach ($id in $classes) { [pscustomobject]@{ hive = 'CurrentUser'; path = "Software\Classes\CLSID\$id" } }
    foreach ($kind in @('*', 'Directory', 'Directory\Background')) { [pscustomobject]@{ hive = 'CurrentUser'; path = "Software\Classes\$kind\shellex\ContextMenuHandlers\TortoiseSCM" } }
    if ($MachineOverlays) {
        [pscustomobject]@{ hive = 'CurrentUser'; path = 'Software\Microsoft\Windows\CurrentVersion\Run'; valueName = 'TortoiseSCMCache' }
        foreach ($id in $classes | Select-Object -Skip 1) { [pscustomobject]@{ hive = 'LocalMachine'; path = "Software\Classes\CLSID\$id" } }
        foreach ($name in @('Normal', 'Modified', 'Conflict', 'Added', 'Deleted', 'Ignored', 'Locked', 'Unversioned')) { [pscustomobject]@{ hive = 'LocalMachine'; path = "Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\TortoiseSCM $name" } }
    }
}

function Read-TscmRegistryTree([Microsoft.Win32.RegistryKey]$Key) {
    $values = @(); $children = @()
    foreach ($name in $Key.GetValueNames()) { $values += [pscustomobject]@{ name = $name; kind = $Key.GetValueKind($name); value = $Key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } }
    foreach ($name in $Key.GetSubKeyNames()) {
        $child = $Key.OpenSubKey($name)
        try { $children += [pscustomobject]@{ name = $name; tree = (Read-TscmRegistryTree $child) } } finally { $child.Dispose() }
    }
    return [pscustomobject]@{ values = $values; children = $children }
}

function Get-TscmRegistrySnapshot($Targets) {
    foreach ($target in $Targets) {
        $hive = if ($target.hive -eq 'CurrentUser') { [Microsoft.Win32.Registry]::CurrentUser } else { [Microsoft.Win32.Registry]::LocalMachine }
        $key = $hive.OpenSubKey($target.path)
        try {
            if ($target.PSObject.Properties['valueName']) {
                $exists = $key -and @($key.GetValueNames()) -contains $target.valueName
                [pscustomobject]@{ hive = $target.hive; path = $target.path; valueName = $target.valueName; exists = [bool]$exists;
                    value = $(if ($exists) { $key.GetValue($target.valueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null });
                    kind = $(if ($exists) { $key.GetValueKind($target.valueName) } else { $null }) }
            } else { [pscustomobject]@{ hive = $target.hive; path = $target.path; tree = $(if ($key) { Read-TscmRegistryTree $key } else { $null }) } }
        }
        finally { if ($key) { $key.Dispose() } }
    }
}

function Write-TscmRegistryTree([Microsoft.Win32.RegistryKey]$Key, $Tree) {
    foreach ($value in $Tree.values) { $Key.SetValue($value.name, $value.value, $value.kind) }
    foreach ($child in $Tree.children) {
        $keyChild = $Key.CreateSubKey($child.name)
        try { Write-TscmRegistryTree $keyChild $child.tree } finally { $keyChild.Dispose() }
    }
}

function Restore-TscmRegistrySnapshot($Snapshot) {
    foreach ($entry in $Snapshot) {
        $hive = if ($entry.hive -eq 'CurrentUser') { [Microsoft.Win32.Registry]::CurrentUser } else { [Microsoft.Win32.Registry]::LocalMachine }
        if ($entry.PSObject.Properties['valueName']) {
            $key = $hive.CreateSubKey($entry.path)
            try {
                if ($entry.exists) { $key.SetValue($entry.valueName, $entry.value, $entry.kind) }
                else { $key.DeleteValue($entry.valueName, $false) }
            } finally { $key.Dispose() }
            continue
        }
        $hive.DeleteSubKeyTree($entry.path, $false)
        if ($entry.tree) {
            $key = $hive.CreateSubKey($entry.path)
            try { Write-TscmRegistryTree $key $entry.tree } finally { $key.Dispose() }
        }
    }
}
