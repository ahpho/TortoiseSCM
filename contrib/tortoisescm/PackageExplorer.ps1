# Explorer refresh helpers. Installation remains successful if refresh is deferred.
function Get-TscmExplorerModules {
    $paths = @()
    $readFailed = $false
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach ($process in [Diagnostics.Process]::GetProcessesByName('explorer')) {
        try {
            if ($process.SessionId -ne $sessionId) { continue }
            foreach ($module in $process.Modules) {
                if ($module.ModuleName -ieq 'TortoiseSCMShell.dll') { $paths += $module.FileName }
            }
        } catch { $readFailed = $true } finally { $process.Dispose() }
    }
    [pscustomobject]@{ Paths = $paths; ReadFailed = $readFailed }
}

function Get-TscmExplorerMenuStatus {
    param([string]$VersionDirectory, [scriptblock]$ModuleCollector = { Get-TscmExplorerModules })
    try {
        $observed = & $ModuleCollector
        if (-not $observed -or -not $observed.PSObject.Properties['ReadFailed']) { throw 'Invalid module observation.' }
        $directories = @($observed.Paths | ForEach-Object { [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($_)).TrimEnd('\') } | Sort-Object -Unique)
        $expected = [IO.Path]::GetFullPath($VersionDirectory).TrimEnd('\')
        $old = @($directories | Where-Object { -not $_.Equals($expected, [StringComparison]::OrdinalIgnoreCase) })
        $state = if ($old.Count) { 'Old' } elseif ($observed.ReadFailed) { 'Unknown' } elseif ($directories.Count) { 'Current' } else { 'NotLoaded' }
        return [pscustomobject]@{ State = $state; OldDirectories = $old }
    } catch { return [pscustomobject]@{ State = 'Unknown'; OldDirectories = @() } }
}

function Write-TscmExplorerMenuStatus {
    param($Status, [string]$VersionDirectory, [switch]$AfterRestart)
    switch ($Status.State) {
        'Old' {
            Write-Host '新版已安装，右键菜单尚未切换。Explorer 仍加载旧组件。' -ForegroundColor Yellow
            foreach ($directory in $Status.OldDirectories) { Write-Host ('Explorer 当前加载的旧目录：' + $directory) -ForegroundColor Yellow }
            Write-Host ('本次安装的新目录：' + $VersionDirectory) -ForegroundColor Yellow
        }
        'Current' { Write-Host '当前 Explorer 已加载本次安装目录的右键组件。' -ForegroundColor Green }
        'NotLoaded' {
            if ($AfterRestart) { Write-Host 'Explorer 已重启，但尚未加载右键组件；请右键打开“版本信息…”核对版本。' -ForegroundColor Yellow }
            else { Write-Host '尚未检测到 Explorer 加载右键组件，无法确认菜单版本；请右键打开“版本信息…”核对。' -ForegroundColor Yellow }
        }
        default { Write-Host '无法自动确认 Explorer 当前加载的右键菜单版本；请通过“版本信息…”核对。' -ForegroundColor Yellow }
    }
    Write-Host '已打开的 TortoiseSCM 窗口需关闭后重新打开。'
}

function Get-TscmExplorerIdentity {
    param($Process)
    # Retain the process handle throughout validation and Kill; never reopen a
    # PID for termination after its ownership was checked.
    $null = $Process.Handle
    if ($Process.HasExited) { throw 'Explorer exited during validation.' }
    $native = Get-WmiObject -Class Win32_Process -Filter ('ProcessId = ' + [int]$Process.Id) -ErrorAction Stop
    if (-not $native) { throw 'Explorer process information is unavailable.' }
    $owner = $native.GetOwnerSid()
    if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrWhiteSpace($owner.Sid)) { throw 'Explorer owner cannot be verified.' }
    [pscustomobject]@{
        Id = $Process.Id; SessionId = $Process.SessionId; Sid = $owner.Sid
        Path = [IO.Path]::GetFullPath($Process.MainModule.FileName)
        Created = $Process.StartTime.ToUniversalTime().Ticks
    }
}

function Test-TscmExplorerIdentity {
    param($Identity, [int]$SessionId, [string]$Sid)
    $expected = [IO.Path]::GetFullPath((Join-Path $env:WINDIR 'explorer.exe'))
    return $Identity.SessionId -eq $SessionId -and $Identity.Sid -eq $Sid -and
        [string]::Equals($Identity.Path, $expected, [StringComparison]::OrdinalIgnoreCase)
}

function Test-TscmExplorerDesktop {
    if (-not ('TortoiseSCM.PackageDesktop' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace TortoiseSCM {
    public static class PackageDesktop {
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
'@
    }
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $shellWindow = [TortoiseSCM.PackageDesktop]::GetShellWindow()
    if ($shellWindow -eq [IntPtr]::Zero) { return $false }
    [uint32]$desktopProcessId = 0
    [TortoiseSCM.PackageDesktop]::GetWindowThreadProcessId($shellWindow, [ref]$desktopProcessId) | Out-Null
    $shellProcess = $null
    try {
        $shellProcess = [Diagnostics.Process]::GetProcessById([int]$desktopProcessId)
        return Test-TscmExplorerIdentity (Get-TscmExplorerIdentity $shellProcess) $sessionId $sid
    } catch { return $false } finally { if ($shellProcess) { $shellProcess.Dispose() } }
}

function Restore-TscmExplorerDesktop {
    param(
        [scriptblock]$ShellReady = { Test-TscmExplorerDesktop },
        [scriptblock]$StartExplorer = { Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -WindowStyle Hidden -ErrorAction Stop | Out-Null },
        [scriptblock]$Wait = { Start-Sleep -Milliseconds 200 }
    )
    # Windows normally recreates the shell itself. Allow that to happen before
    # starting another explorer.exe, which could otherwise open an extra folder.
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        if (& $ShellReady) { return }
        & $Wait | Out-Null
    }
    & $StartExplorer | Out-Null
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        if (& $ShellReady) { return }
        & $Wait | Out-Null
    }
    throw '无法确认桌面和任务栏已恢复。'
}

function Restart-TscmExplorer {
    param(
        [scriptblock]$ProcessCollector = { [Diagnostics.Process]::GetProcessesByName('explorer') },
        [scriptblock]$IdentityReader = { param($process) Get-TscmExplorerIdentity $process },
        [scriptblock]$StopExplorer = { param($process) $process.Kill(); if (-not $process.WaitForExit(5000)) { throw 'Explorer did not exit.' } },
        [scriptblock]$RestoreDesktop = { Restore-TscmExplorerDesktop }
    )
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $processes = @(& $ProcessCollector)
    $targets = @()
    $stopAttempted = $false
    try {
        # Validate the entire target set before stopping even one process.
        foreach ($process in $processes) {
            if ($process.SessionId -ne $sessionId) { continue }
            $identity = & $IdentityReader $process
            if (Test-TscmExplorerIdentity $identity $sessionId $sid) {
                $targets += [pscustomobject]@{ Process = $process; Identity = $identity }
            }
        }
        foreach ($target in $targets) {
            $fresh = & $IdentityReader $target.Process
            if (-not (Test-TscmExplorerIdentity $fresh $sessionId $sid) -or $fresh.Id -ne $target.Identity.Id -or $fresh.Created -ne $target.Identity.Created) {
                throw 'Explorer identity changed; restart was stopped.'
            }
            $stopAttempted = $true
            & $StopExplorer $target.Process | Out-Null
        }
        # Also restore when Explorer exited by itself before enumeration.
        $stopAttempted = $true
    } finally {
        try { if ($stopAttempted) { & $RestoreDesktop | Out-Null } }
        finally { foreach ($process in $processes) { $process.Dispose() } }
    }
}

function Invoke-TscmExplorerRefresh {
    param(
        [string]$VersionDirectory, [switch]$NoRegister, [switch]$NoPause,
        [scriptblock]$ModuleCollector = { Get-TscmExplorerModules },
        [scriptblock]$RestartExplorer = { Restart-TscmExplorer },
        [scriptblock]$CanPrompt = { [Environment]::UserInteractive -and -not [Console]::IsInputRedirected },
        [scriptblock]$AnswerReader = { Read-Host '确认所有文件操作已结束后，按 Enter 重启；输入 N 再回车则稍后' }
    )
    if ($NoRegister) {
        Write-Host '本次未注册右键菜单（-NoRegister），未检查或重启资源管理器。'
        return
    }
    $status = Get-TscmExplorerMenuStatus $VersionDirectory $ModuleCollector
    Write-TscmExplorerMenuStatus $status $VersionDirectory
    if ($status.State -ne 'Old') { return }
    Write-Host '重启资源管理器后菜单才会切换；也可稍后在任务管理器中重启“Windows 资源管理器”。' -ForegroundColor Yellow
    if ($NoPause) { return }
    try { if (-not (& $CanPrompt)) { return } } catch { return }
    Write-Host ''
    Write-Host '注意：重启可能中断资源管理器中的复制、移动、删除、解压等任务，导致文件未完成；请先等待所有这些任务结束。' -ForegroundColor Yellow
    Write-Host '将关闭当前用户的所有文件夹窗口，桌面和任务栏会短暂消失。其他应用不会因此退出。' -ForegroundColor Yellow
    Write-Host '程序未检测这些文件操作是否结束。稍后重启不会撤销已完成的安装。' -ForegroundColor Yellow
    try { $answer = & $AnswerReader } catch { Write-Host '未读取到确认，已保留安装，未重启资源管理器。' -ForegroundColor Yellow; return }
    # EOF/null is not an Enter keystroke; only an actual empty string confirms.
    if ($null -eq $answer -or $answer -isnot [string] -or $answer.Length -ne 0) {
        Write-Host '已选择稍后重启资源管理器；安装已完成。'
        return
    }
    try {
        & $RestartExplorer | Out-Null
        $status = Get-TscmExplorerMenuStatus $VersionDirectory $ModuleCollector
        Write-TscmExplorerMenuStatus $status $VersionDirectory -AfterRestart
        if ($status.State -in @('Old', 'Unknown')) {
            Write-Host '请先结束所有文件操作，再在任务管理器中手动重启“Windows 资源管理器”，然后核对版本。' -ForegroundColor Yellow
        }
    } catch {
        Write-Host ('安装已完成，但资源管理器重启未完成：' + $_.Exception.Message) -ForegroundColor Yellow
        Write-Host '请先结束所有文件操作，再在任务管理器中手动重启“Windows 资源管理器”；若桌面未恢复，可运行新任务 explorer.exe。' -ForegroundColor Yellow
    }
}
