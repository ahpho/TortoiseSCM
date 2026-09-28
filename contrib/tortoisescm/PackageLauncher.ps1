[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Uninstall')][string]$Action,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\TortoiseSCM'),
    [switch]$NoRegister,
    [switch]$NoLaunch,
    [switch]$ConfirmUninstall,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'

function Invoke-TscmPackageLauncher {
    if (-not [Environment]::Is64BitProcess) { throw '请使用 Install.cmd / Uninstall.cmd，或 64 位 Windows PowerShell。' }
    . (Join-Path $PSScriptRoot 'Package.Common.ps1')
    . (Join-Path $PSScriptRoot 'PackageExplorer.ps1')
    $rootPath = Assert-TscmPlainPath $InstallRoot
    $pointerPath = Join-TscmOwnedPath $rootPath 'current-install.json'

    if ($Action -eq 'Install') {
        Write-Host '正在为当前 Windows 用户安装 TortoiseSCM…'
        $enableMachineOverlays = $false
        if (Test-Path -LiteralPath $pointerPath -PathType Leaf) {
            $current = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
            $enableMachineOverlays = [bool]$current.machineOverlays
            if ($enableMachineOverlays -and -not (Test-TscmAdministrator)) {
                throw '现有安装启用了系统级状态图标；升级需要管理员权限。请右键 Install.cmd，选择“以管理员身份运行”。'
            }
            if ($current.PSObject.Properties['modernMenu'] -and $current.modernMenu -and -not (Test-TscmAdministrator)) {
                throw '现有安装启用了现代右键菜单；升级需要管理员权限。请右键 Install.cmd，选择“以管理员身份运行”。'
            }
        }
        $installed = & (Join-Path $PSScriptRoot 'Install.ps1') -PackageDirectory $PSScriptRoot -InstallRoot $rootPath -NoRegister:$NoRegister -EnableMachineOverlays:$enableMachineOverlays
        if (-not $installed -or [string]::IsNullOrWhiteSpace($installed.versionDirectory)) { throw '安装脚本未返回已完成的安装位置。' }
        Write-Host ''
        Write-Host '安装完成。' -ForegroundColor Green
        Write-Host ('安装位置：' + $installed.versionDirectory)
        Invoke-TscmExplorerRefresh -VersionDirectory $installed.versionDirectory -NoRegister:$NoRegister -NoPause:$NoPause
        # Installation never opens the application. Keep -NoLaunch as a compatible no-op.
        return 0
    }

    if ($NoRegister -or $NoLaunch) { throw '-NoRegister 和 -NoLaunch 仅适用于安装。' }
    if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) {
        Write-Host '当前 Windows 用户没有可卸载的 TortoiseSCM 活动安装。'
        return 0
    }
    $current = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($current.versionDirectory)) { throw '当前安装记录不完整；未执行卸载。' }
    $target = Assert-TscmPlainPath $current.versionDirectory
    $versions = Join-TscmOwnedPath $rootPath 'versions'
    if (-not [IO.Path]::GetDirectoryName($target).Equals($versions, [StringComparison]::OrdinalIgnoreCase)) {
        throw '当前安装记录中的目录不属于此安装位置；未执行卸载。'
    }
    $record = [IO.File]::ReadAllText((Join-TscmOwnedPath $target '.tortoisescm-install.json')) | ConvertFrom-Json
    Write-Host '即将卸载当前 Windows 用户的活动安装：'
    Write-Host $target
    Write-Host '工作区、仓库文件和用户设置会保留。'
    if ($record.machineOverlays -and -not (Test-TscmAdministrator)) {
        throw '此安装启用了系统级状态图标，卸载需要管理员权限。请右键 Uninstall.cmd，选择“以管理员身份运行”。'
    }
    if (-not $ConfirmUninstall) {
        $answer = Read-Host '确认卸载？输入 Y 后按回车；其他输入取消'
        if ($answer -notmatch '^(?i:y)$') {
            Write-Host '已取消卸载。'
            return 0
        }
    }
    try {
        $removed = & (Join-Path $PSScriptRoot 'Uninstall.ps1') -InstallRoot $rootPath -VersionDirectory $target -RemoveMachineOverlays:([bool]$record.machineOverlays)
    } catch {
        if ($record.PSObject.Properties['modernMenu'] -and $record.modernMenu -and -not (Test-TscmAdministrator)) {
            Write-Host '此安装包含现代右键菜单；若错误提示权限不足，请右键 Uninstall.cmd，选择“以管理员身份运行”。' -ForegroundColor Yellow
        }
        throw
    }
    if (-not $removed -or -not $removed.PSObject.Properties['removed']) { throw '卸载脚本未返回完成状态，请检查上述信息。' }
    if (-not $removed.removed) {
        Write-Host ''
        Write-Host '卸载尚未完全完成，部分文件正在使用中或已被修改。' -ForegroundColor Yellow
        Write-Host '请关闭 TortoiseSCM，等待所有文件操作结束后重启资源管理器，再运行 Uninstall.cmd。已修改的文件会保留。'
        foreach ($file in $removed.retainedFiles) { Write-Host ('保留：' + $file) }
        return 2
    }
    Write-Host ''
    Write-Host '卸载完成，工作区和用户设置已保留。' -ForegroundColor Green
    Write-Host '如果资源管理器仍显示旧菜单，请等待所有文件操作结束后重启资源管理器，也可注销 Windows 后重新登录。'
    return 0
}

$launcherExitCode = 1
try {
    $launcherExitCode = Invoke-TscmPackageLauncher
} catch {
    Write-Host ''
    Write-Host ('操作未完成：' + $_.Exception.Message) -ForegroundColor Red
} finally {
    if (-not $NoPause) {
        try { Read-Host '按回车关闭此窗口' | Out-Null } catch { }
    }
}
exit $launcherExitCode
