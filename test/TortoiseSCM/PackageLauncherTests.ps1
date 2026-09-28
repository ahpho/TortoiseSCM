# Clickable launchers and isolated coordinator checks. Never register Explorer.
[CmdletBinding()]
param([string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release'), [switch]$SpyOnly)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scripts = Join-Path $repo 'contrib\tortoisescm'
. (Join-Path $scripts 'Package.Common.ps1')
. (Join-Path $scripts 'PackageExplorer.ps1')
$script:assertions = 0
function Assert([bool]$Condition, [string]$Description) {
    $script:assertions++
    if (-not $Condition) { throw $Description }
    Write-Host "PASS: $Description"
}
function Quote-ProcessArgument([string]$Value) {
    if ($Value.Contains('"') -or $Value.EndsWith('\')) { throw 'Test argument requires unsupported escaping.' }
    return '"' + $Value + '"'
}
function Run-Process([string]$Executable, [string]$Arguments, [string]$WorkingDirectory, [string]$InputText = '') {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable; $start.Arguments = $Arguments; $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.RedirectStandardInput = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync(); $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText); $process.StandardInput.Close()
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Package launcher test process timed out.' }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.GetAwaiter().GetResult(); Error = $errorOutput.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
function Run-Coordinator([string]$Package, [string]$Action, [string]$Root, [string]$Options = '', [string]$InputText = '') {
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File ' + (Quote-ProcessArgument (Join-Path $Package 'PackageLauncher.ps1')) +
        ' -Action ' + $Action + ' -InstallRoot ' + (Quote-ProcessArgument $Root) + ' -NoPause ' + $Options
    return Run-Process $powershell $arguments $fixture $InputText
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('TSCM-launcher-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (Test-Path -LiteralPath (Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe')) { $powershell = Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe' }
$registrationBefore = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets $true)) | ConvertTo-Json -Depth 30
try {
    # Read-only production probe catches Windows/PowerShell integration errors
    # that injected restart spies cannot exercise. Headless CI may return false.
    $desktopObserved = Test-TscmExplorerDesktop
    Assert ($desktopObserved -is [bool]) 'Production desktop-shell probe returns a Boolean without restarting Explorer'
    $menuVersion = Join-Path $fixture 'new-version'
    $newDll = Join-Path $menuVersion 'TortoiseSCMShell.dll'
    $oldDll = Join-Path $fixture 'old-version\TortoiseSCMShell.dll'
    $oldModules = { [pscustomobject]@{ Paths = @($oldDll); ReadFailed = $false } }
    Assert ((Get-TscmExplorerMenuStatus $menuVersion $oldModules).State -eq 'Old') 'Explorer detector identifies an old loaded DLL'
    Assert ((Get-TscmExplorerMenuStatus $menuVersion { [pscustomobject]@{ Paths = @($newDll.ToUpperInvariant()); ReadFailed = $false } }).State -eq 'Current') 'Explorer detector compares current DLL directory without case sensitivity'
    Assert ((Get-TscmExplorerMenuStatus $menuVersion { [pscustomobject]@{ Paths = @($newDll, $oldDll); ReadFailed = $false } }).State -eq 'Old') 'Mixed old and current Explorer instances require refresh'
    Assert ((Get-TscmExplorerMenuStatus $menuVersion { [pscustomobject]@{ Paths = @(); ReadFailed = $false } }).State -eq 'NotLoaded') 'No loaded modules does not imply current menu'
    Assert ((Get-TscmExplorerMenuStatus $menuVersion { [pscustomobject]@{ Paths = @($newDll); ReadFailed = $true } }).State -eq 'Unknown') 'Partial read failure cannot claim all Explorer instances are current'
    Assert ((Get-TscmExplorerMenuStatus $menuVersion { throw 'read denied' }).State -eq 'Unknown') 'Module read exception becomes unknown state'
    $script:refreshRestarts = 0
    $script:refreshPrompts = 0
    $restartSpy = { $script:refreshRestarts++ }
    $answerSpy = { $script:refreshPrompts++; return '' }
    $output = Invoke-TscmExplorerRefresh $menuVersion -NoRegister -ModuleCollector { throw 'must not inspect' } -RestartExplorer $restartSpy -CanPrompt { $true } -AnswerReader $answerSpy 6>&1 | Out-String
    Assert ($output.Contains('-NoRegister') -and $script:refreshRestarts -eq 0 -and $script:refreshPrompts -eq 0) 'NoRegister never inspects, prompts or restarts Explorer'
    $output = Invoke-TscmExplorerRefresh $menuVersion -NoPause -ModuleCollector $oldModules -RestartExplorer $restartSpy -CanPrompt { $true } -AnswerReader $answerSpy 6>&1 | Out-String
    Assert ($output.Contains('尚未切换') -and $output.Contains([IO.Path]::GetDirectoryName($oldDll)) -and $output.Contains($menuVersion) -and $script:refreshPrompts -eq 0) 'NoPause reports exact old and new directories without prompting'
    $null = Invoke-TscmExplorerRefresh $menuVersion -ModuleCollector $oldModules -RestartExplorer $restartSpy -CanPrompt { $false } -AnswerReader $answerSpy 6>&1
    Assert ($script:refreshPrompts -eq 0 -and $script:refreshRestarts -eq 0) 'Redirected or noninteractive input cannot confirm an Explorer restart'
    foreach ($answer in @('N', 'R', 'RESTART', 'anything', ' ')) {
        $null = Invoke-TscmExplorerRefresh $menuVersion -ModuleCollector $oldModules -RestartExplorer $restartSpy -CanPrompt { $true } -AnswerReader { $answer } 6>&1
        Assert ($script:refreshRestarts -eq 0) "Nonempty input '$answer' defers Explorer restart"
    }
    foreach ($reader in @({ $null }, { throw 'input failed' })) {
        $null = Invoke-TscmExplorerRefresh $menuVersion -ModuleCollector $oldModules -RestartExplorer $restartSpy -CanPrompt { $true } -AnswerReader $reader 6>&1
        Assert ($script:refreshRestarts -eq 0) 'EOF or failed input never becomes Enter confirmation'
    }
    foreach ($after in @('Current', 'NotLoaded', 'Old', 'Unknown')) {
        $script:refreshReads = 0
        $collector = {
            $script:refreshReads++
            if ($script:refreshReads -eq 1 -or $after -eq 'Old') { return [pscustomobject]@{ Paths = @($oldDll); ReadFailed = $false } }
            return [pscustomobject]@{ Paths = $(if ($after -eq 'Current') { @($newDll) } else { @() }); ReadFailed = $after -eq 'Unknown' }
        }
        $beforeRestarts = $script:refreshRestarts
        $output = Invoke-TscmExplorerRefresh $menuVersion -ModuleCollector $collector -RestartExplorer $restartSpy -CanPrompt { $true } -AnswerReader $answerSpy 6>&1 | Out-String
        Assert ($script:refreshRestarts -eq $beforeRestarts + 1 -and $script:refreshReads -eq 2) "Enter confirms one restart then verifies $after state"
        Assert ($output.Contains('复制、移动、删除、解压') -and $output.Contains('程序未检测') -and $output.Contains('其他应用不会因此退出')) 'Enter confirmation is preceded by explicit file-operation and window-loss warnings'
        if ($after -eq 'Current') { Assert ($output.Contains('当前 Explorer 已加载本次安装目录')) 'Only observed current DLL produces current-menu confirmation' }
        elseif ($after -eq 'NotLoaded') { Assert ($output.Contains('已重启，但尚未加载') -and -not $output.Contains('当前 Explorer 已加载本次安装目录')) 'Restart without loaded DLL asks user to open version information'
        } else { Assert ($output.Contains('任务管理器') -and -not $output.Contains('当前 Explorer 已加载本次安装目录')) 'Unconfirmed restart directs user to manual refresh without claiming current version' }
    }
    $output = Invoke-TscmExplorerRefresh $menuVersion -ModuleCollector $oldModules -RestartExplorer { throw 'simulated stop failure' } -CanPrompt { $true } -AnswerReader { '' } 6>&1 | Out-String
    Assert ($output.Contains('安装已完成，但资源管理器重启未完成') -and $output.Contains('explorer.exe')) 'Restart failure preserves installation success and offers desktop recovery instructions'

    # Exercise restart validation with inert objects. No actual process is killed.
    $testSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $testSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $testExplorer = [IO.Path]::GetFullPath((Join-Path $env:WINDIR 'explorer.exe'))
    $script:stopIds = @(); $script:desktopRestores = 0; $script:disposedProcesses = 0
    function New-ExplorerSpy([int]$Id, [int]$Session) {
        $process = [pscustomobject]@{ Id = $Id; SessionId = $Session }
        $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $script:disposedProcesses++ }
        return $process
    }
    $localExplorer = New-ExplorerSpy 100 $testSession
    $otherSession = New-ExplorerSpy 200 ($testSession + 1)
    $identitySpy = { param($process) [pscustomobject]@{ Id = $process.Id; SessionId = $process.SessionId; Sid = $testSid; Path = $testExplorer; Created = 1 } }
    $stopSpy = { param($process) $script:stopIds += $process.Id }
    $restoreSpy = { $script:desktopRestores++ }
    Restart-TscmExplorer -ProcessCollector { @($localExplorer, $otherSession) } -IdentityReader $identitySpy -StopExplorer $stopSpy -RestoreDesktop $restoreSpy
    Assert ($script:stopIds.Count -eq 1 -and $script:stopIds[0] -eq 100 -and $script:desktopRestores -eq 1 -and $script:disposedProcesses -eq 2) 'Restart targets only current-session Explorer and restores desktop after stopping'
    foreach ($mismatch in @('Sid', 'Path')) {
        $beforeStops = $script:stopIds.Count
        Restart-TscmExplorer -ProcessCollector { @($localExplorer) } -IdentityReader {
            param($process)
            $identity = & $identitySpy $process
            if ($mismatch -eq 'Sid') { $identity.Sid = 'S-1-0-0' } else { $identity.Path = 'C:\untrusted\explorer.exe' }
            return $identity
        } -StopExplorer $stopSpy -RestoreDesktop $restoreSpy
        Assert ($script:stopIds.Count -eq $beforeStops) "Restart never kills Explorer with mismatching $mismatch"
    }
    $beforeStops = $script:stopIds.Count; $beforeRestore = $script:desktopRestores
    $failed = $false
    try { Restart-TscmExplorer -ProcessCollector { @($localExplorer) } -IdentityReader { throw 'owner denied' } -StopExplorer $stopSpy -RestoreDesktop $restoreSpy } catch { $failed = $true }
    Assert ($failed -and $script:stopIds.Count -eq $beforeStops -and $script:desktopRestores -eq $beforeRestore) 'Ownership read failure rejects restart before stopping or restoring anything'
    $script:identityReads = 0; $failed = $false
    try {
        Restart-TscmExplorer -ProcessCollector { @($localExplorer) } -IdentityReader {
            param($process)
            $script:identityReads++; $identity = & $identitySpy $process
            $identity.Created = $script:identityReads
            return $identity
        } -StopExplorer $stopSpy -RestoreDesktop $restoreSpy
    } catch { $failed = $true }
    Assert ($failed -and $script:stopIds.Count -eq $beforeStops -and $script:desktopRestores -eq $beforeRestore) 'Changed process lifetime is rejected immediately before stopping'
    $failed = $false
    try { Restart-TscmExplorer -ProcessCollector { @($localExplorer) } -IdentityReader $identitySpy -StopExplorer { throw 'partial stop failure' } -RestoreDesktop $restoreSpy } catch { $failed = $true }
    Assert ($failed -and $script:desktopRestores -eq $beforeRestore + 1) 'Stop failure still attempts desktop restoration and reports failure'
    $script:desktopStarts = 0; $script:desktopPolls = 0
    Restore-TscmExplorerDesktop -ShellReady { $script:desktopPolls++; $script:desktopPolls -ge 3 } -StartExplorer { $script:desktopStarts++ } -Wait { }
    Assert ($script:desktopStarts -eq 0 -and $script:desktopPolls -eq 3) 'Automatic desktop recovery avoids launching an extra Explorer instance'
    $script:desktopPolls = 0
    Restore-TscmExplorerDesktop -ShellReady { $script:desktopPolls++; $script:desktopPolls -ge 13 } -StartExplorer { $script:desktopStarts++ } -Wait { }
    Assert ($script:desktopStarts -eq 1 -and $script:desktopPolls -eq 13) 'Explicit Explorer start waits until the verified desktop shell returns'
    $script:desktopPolls = 0; $failed = $false
    try { Restore-TscmExplorerDesktop -ShellReady { $script:desktopPolls++; $false } -StartExplorer { $script:desktopStarts++ } -Wait { } } catch { $failed = $true }
    Assert ($failed -and $script:desktopStarts -eq 2 -and $script:desktopPolls -eq 35) 'Desktop recovery timeout reports failure after a bounded poll'
    $output = Invoke-TscmExplorerRefresh $menuVersion -ModuleCollector $oldModules -RestartExplorer {
        Restart-TscmExplorer -ProcessCollector { @($localExplorer) } -IdentityReader $identitySpy -StopExplorer $stopSpy -RestoreDesktop { throw 'desktop recovery failed' }
    } -CanPrompt { $true } -AnswerReader { '' } 6>&1 | Out-String
    Assert ($output.Contains('安装已完成，但资源管理器重启未完成') -and $output.Contains('desktop recovery failed')) 'Desktop restore failure is reported without rolling back installation'

    $spy = Join-Path $fixture '中文 & ! (双击) space'
    [IO.Directory]::CreateDirectory($spy) | Out-Null
    $spySource = @'
param([string]$Action)
$ErrorActionPreference = 'Stop'
@{ action = $Action; is64Bit = [Environment]::Is64BitProcess; directory = $PSScriptRoot; extra = @($args) } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'observed.json') -Encoding UTF8
$exitCode = [int][IO.File]::ReadAllText((Join-Path $PSScriptRoot 'exit-code.txt'))
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'self-remove.flag')) {
    $file = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ($Action + '.cmd')))
    if ([IO.Path]::GetDirectoryName($file) -ne $PSScriptRoot -or [IO.Path]::GetFileName($file) -notin @('Install.cmd', 'Uninstall.cmd')) { throw 'Unsafe spy removal.' }
    Remove-Item -LiteralPath $file -Force
}
exit $exitCode
'@
    [IO.File]::WriteAllText((Join-Path $spy 'PackageLauncher.ps1'), $spySource, (New-Object Text.UTF8Encoding($true)))
    foreach ($action in @('Install', 'Uninstall')) {
        Copy-Item -LiteralPath (Join-Path $scripts ($action + '.cmd')) -Destination $spy
        foreach ($exitCode in @(0, 1, 2)) {
            [IO.File]::WriteAllText((Join-Path $spy 'exit-code.txt'), [string]$exitCode)
            $cmd = Join-Path $spy ($action + '.cmd')
            $arguments = '/d /v:off /s /c "' + (Quote-ProcessArgument $cmd) + ' ignored -Action Opposite"'
            $run = Run-Process $env:ComSpec $arguments $fixture
            $observed = Get-Content -LiteralPath (Join-Path $spy 'observed.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            Assert ($observed.action -eq $action -and $observed.extra.Count -eq 0) "$action.cmd chooses fixed action without forwarding arbitrary arguments"
            Assert ($observed.is64Bit -and $observed.directory -eq $spy) "$action.cmd locates 64-bit PowerShell and sibling coordinator from unrelated current directory"
            Assert ($run.ExitCode -eq $exitCode) "$action.cmd preserves exit code $exitCode with Unicode, spaces, ampersand and exclamation in its path"
        }
    }
    foreach ($cmdHost in @($env:ComSpec, (Join-Path $env:WINDIR 'SysWOW64\cmd.exe'))) {
        if (-not (Test-Path -LiteralPath $cmdHost)) { continue }
        [IO.File]::WriteAllText((Join-Path $spy 'exit-code.txt'), '2')
        $run = Run-Process $cmdHost ('/d /v:on /s /c "' + (Quote-ProcessArgument (Join-Path $spy 'Install.cmd')) + '"') $fixture
        $observed = Get-Content -LiteralPath (Join-Path $spy 'observed.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert ($run.ExitCode -eq 2 -and $observed.is64Bit -and $observed.directory -eq $spy) "Launcher selects x64 PowerShell under $cmdHost with delayed expansion enabled"
    }
    [IO.File]::WriteAllText((Join-Path $spy 'self-remove.flag'), 'remove only copied cmd')
    [IO.File]::WriteAllText((Join-Path $spy 'exit-code.txt'), '2')
    $run = Run-Process $env:ComSpec ('/d /v:off /s /c "' + (Quote-ProcessArgument (Join-Path $spy 'Uninstall.cmd')) + '"') $fixture
    Assert ($run.ExitCode -eq 2 -and -not (Test-Path -LiteralPath (Join-Path $spy 'Uninstall.cmd'))) 'Uninstall.cmd preserves result when its coordinator removes the running batch file'
    if ($SpyOnly) { Write-Output "PASS: $script:assertions package launcher spy assertions"; return }

    $zip = & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory (Join-Path $fixture 'packages') -Version '0.1.0-launcher-test'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Join-Path $fixture '实际安装包 & ! (双击) space'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $package)
    $manifest = Read-TscmManifest $package -VerifyFiles
    foreach ($file in @('Install.cmd', 'Uninstall.cmd', 'PackageLauncher.ps1', 'PackageExplorer.ps1')) { Assert (@($manifest.files | Where-Object path -eq $file).Count -eq 1) "Package manifest owns $file" }
    $installRoot = Join-Path $fixture '安装目录 & ! (双击) space'
    $run = Run-Coordinator $package 'Install' $installRoot '-NoRegister -NoLaunch'
    if ($run.ExitCode -ne 0) { throw ('Coordinator installation failed: ' + $run.Output + $run.Error) }
    Assert ($run.ExitCode -eq 0) 'Coordinator installs verified package into isolated path'
    $pointerPath = Join-Path $installRoot 'current-install.json'
    $pointer = Get-Content -LiteralPath $pointerPath -Encoding UTF8 -Raw | ConvertFrom-Json
    Assert (-not $pointer.registered -and -not $pointer.machineOverlays -and -not $pointer.modernMenu) 'Isolated coordinator install does not request any shell registration'
    $installed = $pointer.versionDirectory
    Read-TscmManifest $installed -VerifyFiles | Out-Null
    foreach ($file in @('Install.cmd', 'Uninstall.cmd', 'PackageLauncher.ps1', 'PackageExplorer.ps1', 'TortoiseSCM.exe')) { Assert (Test-Path -LiteralPath (Join-Path $installed $file)) "Installed version contains $file" }
    $run = Run-Coordinator $package 'Uninstall' $installRoot '' "N`r`n"
    Assert ($run.ExitCode -eq 0 -and (Test-Path -LiteralPath $pointerPath) -and (Test-Path -LiteralPath (Join-Path $installed 'TortoiseSCM.exe'))) 'Declining uninstall preserves active installation and returns success'
    $doc = Join-Path $installed 'doc\TortoiseSCM.md'
    [IO.File]::AppendAllText($doc, "`r`nUser modification kept by uninstall")
    $run = Run-Coordinator $installed 'Uninstall' $installRoot '-ConfirmUninstall'
    Assert ($run.ExitCode -eq 2 -and (Test-Path -LiteralPath $doc) -and (Test-Path -LiteralPath $pointerPath)) 'Modified package file produces partial uninstall exit 2 and retains ownership pointer'
    foreach ($file in @('Uninstall.cmd', 'PackageLauncher.ps1', 'PackageExplorer.ps1', 'Uninstall.ps1', 'package-manifest.json')) { Assert (Test-Path -LiteralPath (Join-Path $installed $file)) "Partial uninstall retains retry component $file" }
    Copy-Item -LiteralPath (Join-Path $package 'doc\TortoiseSCM.md') -Destination $doc -Force
    $run = Run-Coordinator $installed 'Uninstall' $installRoot '-ConfirmUninstall'
    Assert ($run.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $installed) -and -not (Test-Path -LiteralPath $pointerPath)) 'Installed coordinator can remove itself and complete a recovered partial uninstall'
    $run = Run-Coordinator $package 'Uninstall' $installRoot '-ConfirmUninstall'
    Assert ($run.ExitCode -eq 0 -and $run.Output.Length -gt 0 -and -not (Test-Path -LiteralPath $pointerPath)) 'Missing current installation returns a friendly no-op result'
    [IO.File]::AppendAllText((Join-Path $package 'README-PACKAGE.txt'), 'tamper')
    $rejectedRoot = Join-Path $fixture 'rejected-install'
    $run = Run-Coordinator $package 'Install' $rejectedRoot '-NoRegister -NoLaunch'
    Assert ($run.ExitCode -eq 1 -and -not (Test-Path -LiteralPath $rejectedRoot)) 'Coordinator reports package verification error as exit 1 before installing'
    $registrationAfter = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets $true)) | ConvertTo-Json -Depth 30
    Assert ($registrationBefore -ceq $registrationAfter) 'All actual Explorer registration remains unchanged'
    Write-Output "PASS: $script:assertions package launcher assertions"
} finally {
    $checked = Assert-TscmPlainPath $fixture
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\TSCM-launcher-'
    if (-not $checked.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $checked -ne [IO.Path]::GetFullPath($fixture)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}
