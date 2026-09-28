# Reproducible GUI journey regression. Real server writes use fresh autotest fixtures.
# In-process controls and dialogs do not replace Explorer/third-party desktop clicks.
[CmdletBinding()]
param(
    [string]$ReferenceWorkspace,
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [string]$Artifacts,
    [switch]$SkipBuild,
    # Reuse passed stages only in the same artifact directory and source revision.
    [switch]$Resume
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Artifacts) { $Artifacts = Join-Path $repo ('bin\TortoiseSCM\qa\gui-journeys-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$Artifacts = [IO.Path]::GetFullPath($Artifacts)
[IO.Directory]::CreateDirectory($Artifacts) | Out-Null
$binary = Join-Path $repo 'bin\TortoiseSCM\Release'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$setup = @{ CmPath = $CmPath }
if ($ReferenceWorkspace) { $setup.ReferenceWorkspace = $ReferenceWorkspace }
$script:results = @()
$utf8 = New-Object Text.UTF8Encoding($false)
function Read-StageRecords($Value) {
    foreach ($record in $Value) {
        if ($record -is [Array]) { Read-StageRecords $record }
        elseif ($record.stage -and $record.status -in @('running', 'passed', 'failed')) { $record }
        # Recover the value/Count wrapper emitted by earlier Windows PS 5 runs.
        elseif ($record.PSObject.Properties['value']) { Read-StageRecords $record.value }
        else { throw 'Invalid saved stage record. Preserve results.json and start a new run.' }
    }
}
if ($Resume) {
    $resultPath = Join-Path $Artifacts 'results.json'
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Resume requires an existing results.json in -Artifacts.' }
    # Windows PowerShell 5 returns a JSON array as one pipeline object.
    $parsedResults = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $script:results = @(Read-StageRecords $parsedResults)
    if (-not $script:results.Count) { throw 'Resume requires saved stage records.' }
    $previous = Join-Path $Artifacts ('previous-run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    [IO.Directory]::CreateDirectory($previous) | Out-Null
    Get-ChildItem -LiteralPath $Artifacts -File | Where-Object Extension -in @('.log', '.json', '.txt') | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $previous
    }
}

function Stage([string]$Name, [scriptblock]$Action) {
    $prior = @($script:results | Where-Object stage -eq $Name)
    if ($Resume -and $prior.Count -and $prior[-1].status -eq 'passed') {
        Write-Host "Reusing passed stage $Name"; return
    }
    $started = Get-Date
    $log = Join-Path $Artifacts ($Name + '.log')
    Write-Host "Running $Name"
    $record = [ordered]@{ stage = $Name; attempt = $prior.Count + 1; started = $started.ToString('o'); status = 'running'; log = $log }
    $script:results += $record
    try {
        & $Action *> $log
        $record.status = 'passed'
    } catch {
        $record.status = 'failed'; $record.error = $_.ToString()
        throw
    } finally {
        $record.seconds = [Math]::Round(((Get-Date) - $started).TotalSeconds, 2)
        [IO.File]::WriteAllText((Join-Path $Artifacts 'results.json'), (ConvertTo-Json -InputObject @($script:results) -Depth 5), $utf8)
    }
    Write-Host "Passed $Name"
}

# Run one owned GUI test process at a time. Its windows and message pump must not
# compete with other GUI suites. stdout/stderr are retained separately on failure.
function RunGui([string]$Executable, [string[]]$Arguments, [string]$Name) {
    foreach ($argument in $Arguments) {
        if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw 'Unsupported quoted or trailing-backslash test argument.' }
    }
    $commandLine = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable; $start.Arguments = $commandLine
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(900000)) {
            # Only the process created by this suite is stopped, never user apps.
            $process.Kill(); $process.WaitForExit()
            throw "$Name exceeded 15 minutes. Inspect its stdout/stderr."
        }
        $process.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $Artifacts ($Name + '-exit-code.txt')), [string]$process.ExitCode, $utf8)
        if ($process.ExitCode -ne 0) { throw "$Name exited $($process.ExitCode). Inspect its stdout/stderr." }
        ($stdout.GetAwaiter().GetResult() -split '\r?\n') | Select-Object -Last 5
    } finally {
        [IO.File]::WriteAllText((Join-Path $Artifacts ($Name + '-stdout.log')), $stdout.GetAwaiter().GetResult(), $utf8)
        [IO.File]::WriteAllText((Join-Path $Artifacts ($Name + '-stderr.log')), $stderr.GetAwaiter().GetResult(), $utf8)
        $process.Dispose()
    }
}

if (-not $SkipBuild) {
    Stage 'build-regression' {
        & (Join-Path $repo 'build-tortoisescm.ps1') -Test
        if ($LASTEXITCODE -ne 0) { throw 'Build/regression failed.' }
    }
}
Stage 'basic-gui' { & (Join-Path $PSScriptRoot 'Run-BasicGuiAcceptance.ps1') @setup }
Stage 'file-actions-gui' { & (Join-Path $PSScriptRoot 'Run-BasicGuiAcceptance.ps1') @setup -FileActionsOnly }
Stage 'dirty-update-integration' { & (Join-Path $PSScriptRoot 'Run-PartialDirtyUpdateIntegration.ps1') @setup }

Stage 'first-checkout-gui' {
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TortoiseSCM') -Filter '*.cs' | ForEach-Object FullName)
    $sources += @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $sources += Join-Path $PSScriptRoot 'WorkspaceCreationIntegrationTests.cs'
    $output = Join-Path $Artifacts 'WorkspaceCreationIntegrationTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /main:WorkspaceCreationIntegrationTests "/win32icon:$(Join-Path $repo 'src\Resources\gluon.ico')" /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/out:$output" $sources
    if ($LASTEXITCODE -ne 0) { throw 'First checkout GUI compilation failed.' }
    $script:manifest = & (Join-Path $PSScriptRoot 'New-TestWorkspace.ps1') @setup
    if (-not $script:manifest -or -not (Test-Path -LiteralPath $script:manifest)) { throw 'First checkout fixture missing.' }
    [IO.File]::WriteAllText((Join-Path $Artifacts 'wizard-manifest.txt'), $script:manifest, $utf8)
    RunGui $output @($script:manifest, $CmPath) 'first-checkout'
}
$script:manifest = Get-Content -LiteralPath (Join-Path $Artifacts 'wizard-manifest.txt') -Raw -Encoding UTF8
$fixture = Get-Content -LiteralPath $script:manifest -Raw -Encoding UTF8 | ConvertFrom-Json
Stage 'commit-message-gui' {
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TortoiseSCM') -Filter '*.cs' | ForEach-Object FullName)
    $sources += @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $sources += Join-Path $PSScriptRoot 'CommitMessageIntegrationTests.cs'
    $output = Join-Path $Artifacts 'CommitMessageIntegrationTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /main:CommitMessageIntegrationTests "/win32icon:$(Join-Path $repo 'src\Resources\gluon.ico')" /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/out:$output" $sources
    if ($LASTEXITCODE -ne 0) { throw 'Commit message GUI compilation failed.' }
    $messageManifest = & (Join-Path $PSScriptRoot 'New-TestWorkspace.ps1') @setup
    if (-not $messageManifest -or -not (Test-Path -LiteralPath $messageManifest)) { throw 'Commit message fixture missing.' }
    [IO.File]::WriteAllText((Join-Path $Artifacts 'commit-message-manifest.txt'), $messageManifest, $utf8)
    RunGui $output @($messageManifest, $CmPath) 'commit-message'
}
Stage 'live-workspace-ui' { RunGui (Join-Path $binary 'UiTests.exe') @((Join-Path $Artifacts 'live-ui'), $fixture.producer) 'live-ui' }
Stage 'live-blame-ui' {
    # Keep this PS1 ASCII-safe when invoked by Windows PowerShell 5 without a BOM.
    $fileName = 'selected ' + [char]0x4e2d + [char]0x6587 + '.txt'
    RunGui (Join-Path $binary 'UiTests.exe') @('--blame-live', (Join-Path $Artifacts 'blame'), (Join-Path $fixture.producer $fileName), $fixture.producer) 'blame'
}
Stage 'live-graph-ui' { RunGui (Join-Path $binary 'UiTests.exe') @('--graph-live', (Join-Path $Artifacts 'graph'), $fixture.producer) 'graph' }
Stage 'shell-production-dll' {
    & (Join-Path $binary 'ShellTests.exe') --modern-dll $binary
    if ($LASTEXITCODE -ne 0) { throw 'Shell production DLL tests failed.' }
}
foreach ($suite in @('PackageTests', 'ModernPackageTests')) {
    Stage $suite { & (Join-Path $PSScriptRoot ($suite + '.ps1')) }
}
# These supplement GUI state/confirmation tests with independent native server
# outcomes. They are reported as CLI integration, not GUI clicks.
foreach ($suite in @('BranchIntegrationTests', 'BranchRenameIntegrationTests', 'BranchDeleteIntegrationTests', 'PartialBranchSwitchIntegrationTests', 'ShelvesIntegrationTests', 'LabelIntegrationTests')) {
    Stage $suite {
        $advancedManifest = & (Join-Path $PSScriptRoot 'New-TestWorkspace.ps1') @setup
        if (-not $advancedManifest -or -not (Test-Path -LiteralPath $advancedManifest)) { throw "$suite fixture missing." }
        [IO.File]::WriteAllText((Join-Path $Artifacts ($suite + '-manifest.txt')), $advancedManifest, $utf8)
        $advanced = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $advancedManifest
        if ($suite -eq 'LabelIntegrationTests') {
            # Labels need a real committed snapshot, not the empty cs:0 fixture.
            $seed = Join-Path $advanced.producer 'label-fixture.txt'
            [IO.File]::WriteAllText($seed, "Isolated label snapshot`n", $utf8)
            Push-Location -LiteralPath $advanced.producer
            try {
                & $CmPath add $seed
                if ($LASTEXITCODE -ne 0) { throw 'Label fixture add failed.' }
                & $CmPath checkin $seed '-c=GUI journey isolated label snapshot'
                if ($LASTEXITCODE -ne 0) { throw 'Label fixture checkin failed.' }
            } finally { Pop-Location }
        }
        $parameters = @{ Manifest = $advancedManifest; CmPath = $CmPath; Executable = (Join-Path $binary 'TortoiseSCM.exe') }
        if ($suite -eq 'LabelIntegrationTests') { $parameters.UiExecutable = Join-Path $binary 'UiTests.exe' }
        & (Join-Path $PSScriptRoot ($suite + '.ps1')) @parameters
        $advanced = Get-Content -LiteralPath $advancedManifest -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($suite -eq 'BranchIntegrationTests') {
            RunGui (Join-Path $binary 'UiTests.exe') @('--branches-live', (Join-Path $Artifacts 'branches'), $advanced.producer) 'branches'
        }
        if ($suite -eq 'ShelvesIntegrationTests') {
            $shelves = Get-Content -LiteralPath (Join-Path $advanced.runDirectory 'shelves-fixture.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($shelve in $shelves) {
                $name = 'shelves-' + $shelve.role
                RunGui (Join-Path $binary 'UiTests.exe') @('--shelves-live', (Join-Path $Artifacts $name), $advanced.($shelve.role), [string]$shelve.shelveId) $name
            }
        }
    }
}
[IO.File]::WriteAllText((Join-Path $Artifacts 'results.json'), (ConvertTo-Json -InputObject @($script:results) -Depth 5), $utf8)
Write-Output "GUI journey evidence: $Artifacts"
