# Exercises the actual Inno installer and uninstaller, using only TestSetup builds.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BeyondCompareDirectory,
    [string]$TortoiseToolsDirectory,
    [string]$BinaryDirectory = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release'),
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scripts = Join-Path $repo 'contrib/tortoisescm'
. (Join-Path $scripts 'Package.Common.ps1')
$script:assertions = 0
function Assert([bool]$Condition, [string]$Description) {
    $script:assertions++
    if (-not $Condition) { throw $Description }
    Write-Host ('PASS: ' + $Description)
}
$testArp = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{D7305D7A-348E-4E30-88D3-6BCE9CE856BA}_is1'
if (Test-Path $testArp) { throw 'A TestSetup installation already exists. Remove it before starting this isolated acceptance run.' }
$fixture = Assert-TscmPlainPath (Join-Path $repo ('bin/TortoiseSCM/qa/setup-native-' + [Guid]::NewGuid().ToString('N')))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$root = Join-Path $fixture '安装目录 with spaces & symbols'
$productionRoot = Join-Path $env:LOCALAPPDATA 'Programs/TortoiseSCM'
$productionPointer = Join-Path $productionRoot 'current-install.json'
$beforePointerHash = if (Test-Path -LiteralPath $productionPointer) { (Get-FileHash -LiteralPath $productionPointer -Algorithm SHA256).Hash } else { '' }
$productionArp = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B30D80DC-C4F3-4CD7-8175-86D48C0896A4}_is1'
$beforeArp = Test-Path $productionArp
$uninstaller = Join-Path $root 'setup/unins000.exe'
$versions = @(('setup-native-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '-a'), ('setup-native-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '-b'))
function Run-Installer([string]$Executable, [string]$LogName, [bool]$Install, [string]$Directory = $root) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $fixture ($LogName + '.log')) + '"'))
    if ($Install -and $Directory) { $arguments += ('/DIR="' + $Directory + '"') }
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
$lock = $null
try {
    $setups = @()
    foreach ($version in $versions) {
        $setups += & (Join-Path $scripts 'Build-Setup.ps1') -BeyondCompareDirectory $BeyondCompareDirectory -TortoiseToolsDirectory $TortoiseToolsDirectory -BinaryDirectory $BinaryDirectory -OutputDirectory $fixture -Version $version -IsccPath $IsccPath -TestSetup
    }
    Assert ($setups.Count -eq 2) 'Two test installers with distinct package versions compiled.'
    Assert (@($setups | Where-Object { $_ -notlike '*-Setup-Test.exe' }).Count -eq 0) 'Only test installer executable names may execute.'
    Assert ((Run-Installer $setups[0] 'install-a' $true) -eq 0) 'First real installer returns success.'
    $first = [IO.File]::ReadAllText((Join-Path $root 'current-install.json')) | ConvertFrom-Json
    Assert (-not $first.registered) 'TestSetup does not register Explorer shell extensions.'
    Assert ([string]$first.installRoot -eq $root) 'Unicode, spaces and ampersand install path is passed intact.'
    Assert ((Read-TscmManifest $first.versionDirectory -VerifyFiles).version -eq $versions[0]) 'First installed payload hashes and version are correct.'
    Assert (Test-Path $testArp) 'Installer registers the independent test Apps entry.'
    Assert ((Get-ItemProperty $testArp).DisplayName -like 'TortoiseSCM Installer Test *') 'Test Apps entry is visibly distinguished from production.'
    Assert (Test-Path -LiteralPath $uninstaller) 'Windows Apps uninstaller is present.'
    $otherRoot = Join-Path $fixture 'unreviewed-relocation'
    $pointerBefore = [IO.File]::ReadAllText((Join-Path $root 'current-install.json'))
    Assert ((Run-Installer $setups[1] 'reject-relocation' $true $otherRoot) -ne 0) 'An upgrade cannot silently strand the existing installation by changing /DIR.'
    Assert (-not (Test-Path -LiteralPath $otherRoot)) 'Rejected relocation creates no second install root.'
    Assert ([IO.File]::ReadAllText((Join-Path $root 'current-install.json')) -eq $pointerBefore) 'Rejected relocation preserves the active installation.'
    Assert (Test-Path -LiteralPath (Join-Path $first.versionDirectory 'Tools/BeyondCompare/BComp.exe')) 'Beyond Compare is installed with the payload.'
    if ($TortoiseToolsDirectory) {
        foreach ($file in @('TortoiseGitMerge.exe', 'TortoiseGitUDiff.exe', 'LICENSE.txt', 'TortoiseGit-source.zip', 'mfc140u.dll')) {
            $installedFile = Join-Path $first.versionDirectory ('Tools/TortoiseGit/' + $file)
            Assert ((Get-FileHash -LiteralPath $installedFile).Hash -eq (Get-FileHash -LiteralPath (Join-Path $TortoiseToolsDirectory $file)).Hash) "Native tool payload matches source: $file"
        }
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path $first.versionDirectory 'TortoiseSCM.exe'
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $config = Join-Path $fixture 'isolated-settings.xml'
        foreach ($provider in @('', 'BeyondCompare', 'TortoiseMerge')) {
            $start.Arguments = '--cli --command settings --settings-file "' + $config + '" --json'
            if ($provider) { $start.Arguments += ' --tool-provider ' + $provider + ' --yes' }
            $process = [Diagnostics.Process]::Start($start)
            try {
                $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Installed profile query timed out.' }
                $json = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
                $expected = if ($provider) { $provider } else { 'TortoiseMerge' }
                Assert ($process.ExitCode -eq 0 -and $json.data.settings.toolProvider -eq $expected) "Installed app selects profile: $expected"
            } finally { $process.Dispose() }
        }
        # Real native CLI operation loads the app-local runtime without any tool installation or GUI editing.
        $before = Join-Path $fixture 'before.txt'; $after = Join-Path $fixture 'after.txt'; $patch = Join-Path $fixture 'native.diff'
        [IO.File]::WriteAllText($before, "before`n"); [IO.File]::WriteAllText($after, "after`n")
        $native = Join-Path $first.versionDirectory 'Tools/TortoiseGit/TortoiseGitMerge.exe'
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $native; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.Arguments = '/createunifieddiff /origfile:"' + $before + '" /modifiedfile:"' + $after + '" /outfile:"' + $patch + '"'
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Native diff smoke test timed out.' }
            Assert ($process.ExitCode -eq 0 -and (Test-Path -LiteralPath $patch)) 'Installed native executable creates an actual unified diff.'
            Assert ([IO.File]::ReadAllText($patch).Contains('-before') -and [IO.File]::ReadAllText($patch).Contains('+after')) 'Installed native diff contains expected source and target lines.'
        } finally { $process.Dispose() }
    }
    $oldDll = Join-Path $first.versionDirectory 'TortoiseSCMShell.dll'
    $lock = [IO.File]::Open($oldDll, 'Open', 'Read', 'Read')
    Assert ((Run-Installer $setups[1] 'upgrade-b' $true '') -eq 0) 'A newer installer remembers the custom path without /DIR and upgrades while the old shell DLL is locked.'
    $second = [IO.File]::ReadAllText((Join-Path $root 'current-install.json')) | ConvertFrom-Json
    Assert ($first.versionDirectory -ne $second.versionDirectory) 'Upgrade publishes a fresh version directory.'
    Assert ((Read-TscmManifest $second.versionDirectory -VerifyFiles).version -eq $versions[1]) 'Upgrade selects the newer package version with verified files.'
    Assert (Test-Path -LiteralPath $oldDll) 'Old loaded shell DLL is preserved during upgrade.'
    Assert ((Get-ItemProperty $testArp).DisplayVersion -eq $versions[1]) 'Windows Apps entry reflects the new version.'
    Assert ((Run-Installer $setups[1] 'reinstall-b' $true) -eq 0) 'Reinstalling the same newer installer is supported.'
    $third = [IO.File]::ReadAllText((Join-Path $root 'current-install.json')) | ConvertFrom-Json
    Assert ($third.versionDirectory -ne $second.versionDirectory) 'Same-version reinstall also avoids overwriting a loaded payload.'
    Assert ((Run-Installer $uninstaller 'uninstall-locked' $false) -ne 0) 'Uninstaller returns failure when an older version remains locked.'
    Assert (Test-Path $testArp) 'Failed uninstall preserves the Windows Apps entry for retry.'
    Assert (Test-Path -LiteralPath $uninstaller) 'Failed uninstall preserves its executable.'
    Assert (Test-Path -LiteralPath (Join-Path $root 'setup/SetupBridge.ps1')) 'Failed uninstall preserves support scripts.'
    Assert (Test-Path -LiteralPath $oldDll) 'Failed uninstall preserves the locked file.'
    Assert (([IO.File]::ReadAllText((Join-Path $root 'current-install.json')) | ConvertFrom-Json).versionDirectory -eq $third.versionDirectory) 'Blocked uninstall preserves the active pointer.'
    foreach ($installedVersion in @($first, $second, $third)) {
        Read-TscmManifest $installedVersion.versionDirectory -VerifyFiles | Out-Null
    }
    Assert ($true) 'Blocked uninstall preserves all files of every installed version.'
    Assert (-not ([IO.File]::ReadAllText((Join-Path $fixture 'uninstall-locked.log')).Contains('Runtime error'))) 'Expected file contention does not produce a Pascal runtime error.'
    $lock.Dispose(); $lock = $null
    Assert ((Run-Installer $uninstaller 'uninstall-retry' $false) -eq 0) 'The same Windows Apps uninstaller completes on retry after releasing the file.'
    Assert (-not (Test-Path $testArp)) 'Successful uninstall removes only the test Apps entry.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'current-install.json'))) 'Successful uninstall removes the active version pointer.'
    Assert (-not (Test-Path -LiteralPath $oldDll)) 'Successful retry removes the formerly locked old DLL.'
    $uiTest = Join-Path $fixture 'SetupWizardUiTests.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Drawing.dll "/out:$uiTest" (Join-Path $PSScriptRoot 'SetupWizardUiTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Setup wizard UI test compilation failed.' }
    & $compiler /nologo /target:winexe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$(Join-Path $fixture 'SetupLockHost.exe')" (Join-Path $PSScriptRoot 'SetupLockHost.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Setup lock host compilation failed.' }
    & $uiTest $setups[1] (Join-Path $fixture 'wizard-ui')
    Assert ($LASTEXITCODE -eq 0) 'Real wizard supports destination selection, repair and confirmed/cancelled maintenance uninstall.'
    $afterPointerHash = if (Test-Path -LiteralPath $productionPointer) { (Get-FileHash -LiteralPath $productionPointer -Algorithm SHA256).Hash } else { '' }
    Assert ($beforePointerHash -eq $afterPointerHash) 'Existing production installation pointer is unchanged.'
    Assert ($beforeArp -eq (Test-Path $productionArp)) 'Production Windows Apps entry presence is unchanged.'
    [pscustomobject]@{ assertions = $script:assertions; success = $true; artifacts = $fixture; productionUnchanged = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'results.json') -Encoding UTF8
    Write-Host ("PASS: {0} native installer assertions. Artifacts: {1}" -f $script:assertions, $fixture)
} finally {
    if ($lock) { $lock.Dispose() }
    # Only our fixed test AppId and recorded fixture-owned executable may be cleaned up.
    if ((Test-Path $testArp) -and (Test-Path -LiteralPath $uninstaller)) {
        $recordedRoot = [string](Get-ItemProperty $testArp).InstallLocation
        if ($recordedRoot.TrimEnd('\') -eq $root.TrimEnd('\')) { Run-Installer $uninstaller 'cleanup-test' $false | Out-Null }
    }
}
