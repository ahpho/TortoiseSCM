[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Uninstall', 'RestartExplorer')][string]$Action,
    [string]$PackageArchive,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [switch]$NoRegister
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
. (Join-Path $PSScriptRoot 'PackageExplorer.ps1')

function Write-SetupResult([bool]$Success, [string]$Message, [string]$Directory, [string]$ExplorerState) {
    # Windows INI readers consume UTF-16 without depending on the system codepage.
    $lines = @('[Result]', ('Success=' + [int]$Success))
    foreach ($entry in @(@('Message', $Message), @('VersionDirectory', $Directory), @('ExplorerState', $ExplorerState))) {
        $value = ([string]$entry[1]).Replace("`r", ' ').Replace("`n", ' ')
        $lines += ([string]$entry[0] + '=' + $value)
    }
    [IO.File]::WriteAllText($ResultPath, ($lines -join "`r`n"), [Text.Encoding]::Unicode)
    $script:lastSetupResult = @($Success, $Message, $Directory, $ExplorerState)
}

function Expand-SetupPackage([string]$ArchivePath, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead((Assert-TscmPlainPath $ArchivePath))
    try {
        $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.TrimEnd('/')
            if (-not $seen.Add($relative)) { throw '安装包包含重复路径。' }
            $target = Join-TscmOwnedPath $Destination $relative
            if ($entry.FullName.EndsWith('/')) { [IO.Directory]::CreateDirectory($target) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    } finally { $archive.Dispose() }
}

function Read-SetupOwnedRecord([string]$Root, [string]$Target) {
    $targetPath = Assert-TscmPlainPath $Target
    $versions = Join-TscmOwnedPath $Root 'versions'
    if (-not [IO.Path]::GetDirectoryName($targetPath).Equals($versions, [StringComparison]::OrdinalIgnoreCase)) {
        throw '安装记录指向了安装根目录以外的位置；未执行操作。'
    }
    $record = [IO.File]::ReadAllText((Join-TscmOwnedPath $targetPath '.tortoisescm-install.json')) | ConvertFrom-Json
    $manifest = Join-TscmOwnedPath $targetPath 'package-manifest.json'
    if ($record.schemaVersion -ne 1 -or $record.product -ne 'TortoiseSCM' -or $record.installRoot -ne $Root -or
        $record.versionDirectory -ne $targetPath -or $record.packageManifestSha256 -ne (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash) {
        throw '安装所有权记录或清单已改变；未执行操作。'
    }
    Read-TscmManifest $targetPath | Out-Null
    return $record
}

$exitCode = 1
$stage = $null
try {
    if (-not [Environment]::Is64BitProcess) { throw '安装需要 64 位 Windows PowerShell。' }
    $rootPath = Assert-TscmPlainPath $InstallRoot
    $ResultPath = Assert-TscmPlainPath $ResultPath
    if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($ResultPath)) -PathType Container)) { throw '安装结果目录不存在。' }
    $pointerPath = Join-TscmOwnedPath $rootPath 'current-install.json'
    if ($Action -eq 'Install') {
        if ([string]::IsNullOrWhiteSpace($PackageArchive)) { throw '缺少随包安装文件。' }
        $overlays = $false
        if (Test-Path -LiteralPath $pointerPath -PathType Leaf) {
            $pointer = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
            $previous = Read-SetupOwnedRecord $rootPath $pointer.versionDirectory
            if ($NoRegister -and $previous.registered) { throw '隔离测试安装不能覆盖已注册的活动安装。' }
            $overlays = [bool]$previous.machineOverlays
            if (($overlays -or ($previous.PSObject.Properties['modernMenu'] -and $previous.modernMenu)) -and -not (Test-TscmAdministrator)) {
                throw '现有安装启用了系统级状态图标或现代菜单；请关闭安装程序，右键安装包选择“以管理员身份运行”后重试。'
            }
        }
        $stage = Join-Path ([IO.Path]::GetTempPath()) ('TortoiseSCM-Setup-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        Expand-SetupPackage $PackageArchive $stage
        $manifest = Read-TscmManifest $stage -VerifyFiles
        $hasNative = @($manifest.files | Where-Object path -eq 'Tools/TortoiseGit/TortoiseGitMerge.exe').Count -eq 1
        $requiredTools = if ($hasNative) { @('Tools/TortoiseGit/TortoiseGitMerge.exe', 'Tools/TortoiseGit/TortoiseGitUDiff.exe', 'Tools/TortoiseGit/LICENSE.txt', 'Tools/TortoiseGit/TortoiseGit-source.zip') } else { @('Tools/BeyondCompare/BComp.exe', 'Tools/BeyondCompare/BCompare.exe', 'Tools/BeyondCompare/License.html') }
        foreach ($required in $requiredTools) {
            if (@($manifest.files | Where-Object path -EQ $required).Count -ne 1) { throw '此图形安装包缺少比较工具运行文件，请使用完整安装包。' }
        }
        $installed = & (Join-Path $stage 'Install.ps1') -PackageDirectory $stage -InstallRoot $rootPath -NoRegister:$NoRegister -EnableMachineOverlays:$overlays
        if (-not $installed -or [string]::IsNullOrWhiteSpace($installed.versionDirectory)) { throw '安装未返回成功结果。' }
        $message = '安装成功。比较和合并工具已随包安装，可在设置中选择。'
        # Optional narrow migration of a previously selected bundled-tool path is
        # added by SetupBeyondCompare.ps1. External user tool choices stay intact.
        $migration = Join-Path $PSScriptRoot 'SetupBeyondCompare.ps1'
        if (Test-Path -LiteralPath $migration) {
            try {
                . $migration
                if (Get-Command Update-TscmSetupBeyondCompare -ErrorAction SilentlyContinue) {
                    $migrationResult = Update-TscmSetupBeyondCompare -InstallRoot $rootPath -VersionDirectory $installed.versionDirectory -NoRegister:$NoRegister
                    if ($migrationResult -and $migrationResult.Warning) { $message += ' ' + $migrationResult.Warning }
                }
            } catch { $message += ' 原有工具设置未迁移：' + $_.Exception.Message }
        }
        $state = if ($NoRegister) { 'NotLoaded' } else { (Get-TscmExplorerMenuStatus $installed.versionDirectory).State }
        Write-SetupResult $true $message $installed.versionDirectory $state
        $exitCode = 0
    } elseif ($Action -eq 'Uninstall') {
        $targets = @()
        if (Test-Path -LiteralPath $pointerPath -PathType Leaf) {
            $pointer = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
            $targets += [string]$pointer.versionDirectory
        }
        $versions = Join-TscmOwnedPath $rootPath 'versions'
        if (Test-Path -LiteralPath $versions -PathType Container) {
            foreach ($directory in Get-ChildItem -LiteralPath $versions -Directory) {
                if (Test-Path -LiteralPath (Join-Path $directory.FullName '.tortoisescm-install.json') -PathType Leaf) {
                    if ($targets -notcontains $directory.FullName) { $targets += $directory.FullName }
                }
            }
        }
        # Validate every version before deleting the first. Old in-use DLLs can
        # remain after upgrades, so a retry must work even without current-install.
        $records = @($targets | ForEach-Object { Read-SetupOwnedRecord $rootPath $_ })
        foreach ($record in $records) {
            if ($NoRegister -and $record.registered) { throw '隔离测试卸载不能注销活动菜单。' }
            if ($record.machineOverlays -and -not (Test-TscmAdministrator)) { throw '此安装启用了系统级状态图标，请以管理员身份运行卸载程序。' }
        }
        $retained = @()
        foreach ($record in $records) {
            $removed = & (Join-Path $PSScriptRoot 'Uninstall.ps1') -InstallRoot $rootPath -VersionDirectory $record.versionDirectory -RemoveMachineOverlays:([bool]$record.machineOverlays) -NoUnregister:$NoRegister
            if (-not $removed.removed) { $retained += @($removed.retainedFiles) }
        }
        if ($retained.Count) {
            Write-SetupResult $false '部分文件仍在使用中或已被修改。菜单已尽可能注销；卸载入口将保留。请关闭 TortoiseSCM 和 Beyond Compare，等待文件操作结束后重启资源管理器，再从“已安装的应用”重试卸载。修改过的文件会保留。' '' 'Unknown'
            $exitCode = 2
        } else {
            Write-SetupResult $true '卸载成功。工作区和用户设置已保留。' '' 'NotLoaded'
            $exitCode = 0
        }
    } else {
        if ($NoRegister) { throw '隔离测试模式禁止重启资源管理器。' }
        if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) { throw '没有可用于核对菜单版本的活动安装。' }
        $pointer = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
        $record = Read-SetupOwnedRecord $rootPath $pointer.versionDirectory
        Restart-TscmExplorer
        $state = (Get-TscmExplorerMenuStatus $record.versionDirectory).State
        Write-SetupResult $true '资源管理器已重启；可通过右键“版本信息”核对版本。' $record.versionDirectory $state
        $exitCode = 0
    }
} catch {
    try { Write-SetupResult $false $_.Exception.Message '' 'Unknown' } catch { Write-Error $_ }
    $exitCode = 1
} finally {
    try {
        if ($stage -and (Test-Path -LiteralPath $stage)) {
            $checkedStage = Assert-TscmPlainPath $stage
            $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
            if ([IO.Path]::GetDirectoryName($checkedStage) -ne $tempRoot -or [IO.Path]::GetFileName($checkedStage) -notmatch '^TortoiseSCM-Setup-[a-f0-9]{32}$') { throw 'Unsafe setup stage cleanup path.' }
            Remove-Item -LiteralPath $checkedStage -Recurse -Force
        }
    } catch {
        # Cleanup cannot undo a committed installation or replace its result.
        $warning = ' 临时安装文件未能清理，可稍后删除：' + $stage
        Write-Warning ($warning + ' (' + $_.Exception.Message + ')')
        if ($script:lastSetupResult) {
            try {
                $last = $script:lastSetupResult
                Write-SetupResult $last[0] ($last[1] + $warning) $last[2] $last[3]
            } catch { Write-Warning ('无法记录临时文件清理提示：' + $_.Exception.Message) }
        }
    }
}
exit $exitCode
