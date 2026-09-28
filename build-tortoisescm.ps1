param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Test,
    [string]$Workspace,
    [switch]$Integration
)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 or newer with C++ and .NET Framework 4.8 tools is required.' }
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild with the C++ workload was not found.' }
& $msbuild (Join-Path $PSScriptRoot 'src\TortoiseSCM.sln') /m /nologo /verbosity:minimal "/p:Configuration=$Configuration" /p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "TortoiseSCM build failed ($LASTEXITCODE)." }
$out = Join-Path $PSScriptRoot "bin\TortoiseSCM\$Configuration"
if ($Test -or $Integration) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $testOutput = Join-Path $out 'TortoiseSCM.BackendTests.exe'
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $sources += Join-Path $PSScriptRoot 'test\TortoiseSCM\BackendTests.cs'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll "/out:$testOutput" $sources
    if ($LASTEXITCODE -ne 0) { throw 'Backend test compilation failed.' }
    if ($Workspace) { & $testOutput $Workspace } else { & $testOutput }
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
    $toolOutput = Join-Path $out 'ToolTests.exe'
    $toolSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $toolSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\ToolTests.cs'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll "/out:$toolOutput" $toolSources
    if ($LASTEXITCODE -ne 0) { throw 'External tool test compilation failed.' }
    & $toolOutput
    if ($LASTEXITCODE -ne 0) { throw 'External tool tests failed.' }
    $revisionOutput = Join-Path $out 'RevisionTests.exe'
    $revisionSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $revisionSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\RevisionTests.cs'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll "/out:$revisionOutput" $revisionSources
    if ($LASTEXITCODE -ne 0) { throw 'Revision test compilation failed.' }
    & $revisionOutput
    if ($LASTEXITCODE -ne 0) { throw 'Revision tests failed.' }
    foreach ($suite in @('TextComparisonTests', 'TextMergePlanTests', 'BuiltInToolTests', 'BeyondCompareTests', 'BeyondCompareProcessTests', 'WorkingBeyondCompareTests', 'ShelveBeyondCompareTests', 'HistoricalBeyondCompareTests', 'CheckinPreflightTests', 'WorkspaceRollbackTests', 'FileOperationTests', 'HistoricalFileTests', 'RepositoryBrowserTests', 'ChangesetComparisonTests', 'BranchTests', 'BranchRenameTests', 'BranchHierarchyTests', 'LabelTests', 'RevisionGraphTests', 'ShelvesTests', 'BlameTests', 'LockTests', 'MergeTests', 'DirectoryMergeTests', 'OverlayTests', 'HistoryTests')) {
        $suiteOutput = Join-Path $out "$suite.exe"
        $suiteSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
        $suiteSources += Join-Path $PSScriptRoot "test\TortoiseSCM\$suite.cs"
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll "/out:$suiteOutput" $suiteSources
        if ($LASTEXITCODE -ne 0) { throw "$suite compilation failed." }
        & $suiteOutput
        if ($LASTEXITCODE -ne 0) { throw "$suite failed." }
    }
    $cliOutput = Join-Path $out 'CliTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$cliOutput" (Join-Path $PSScriptRoot 'test\TortoiseSCM\CliTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'CLI test compilation failed.' }
    & $cliOutput (Join-Path $out 'TortoiseSCM.exe')
    if ($LASTEXITCODE -ne 0) { throw 'CLI black-box tests failed.' }
    $blameCliOutput = Join-Path $out 'BlameCliTests.exe'
    $blameCliSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $blameCliSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\BlameCliTests.cs'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Web.Extensions.dll "/out:$blameCliOutput" $blameCliSources
    if ($LASTEXITCODE -ne 0) { throw 'Blame CLI test compilation failed.' }
    & $blameCliOutput (Join-Path $out 'TortoiseSCM.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Blame CLI tests failed.' }
    $shelveContentOutput = Join-Path $out 'ShelveCompareExportCliTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$shelveContentOutput" (Join-Path $PSScriptRoot 'test\TortoiseSCM\ShelveCompareExportCliTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Shelveset content CLI test compilation failed.' }
    & $shelveContentOutput (Join-Path $out 'TortoiseSCM.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Shelveset content CLI tests failed.' }
    $browserCliOutput = Join-Path $out 'RepositoryBrowserCliTests.exe'
    $browserCliSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $browserCliSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\RepositoryBrowserCliTests.cs'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$browserCliOutput" $browserCliSources
    if ($LASTEXITCODE -ne 0) { throw 'Repository browser CLI test compilation failed.' }
    & $browserCliOutput (Join-Path $out 'TortoiseSCM.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Repository browser CLI tests failed.' }
    $labelCliOutput = Join-Path $out 'LabelCliTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$labelCliOutput" (Join-Path $PSScriptRoot 'test\TortoiseSCM\LabelCliTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Label CLI test compilation failed.' }
    & $labelCliOutput (Join-Path $out 'TortoiseSCM.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Label CLI tests failed.' }
    $graphCliOutput = Join-Path $out 'RevisionGraphCliTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$graphCliOutput" (Join-Path $PSScriptRoot 'test\TortoiseSCM\RevisionGraphCliTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Revision graph CLI test compilation failed.' }
    & $graphCliOutput (Join-Path $out 'TortoiseSCM.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Revision graph CLI tests failed.' }
    & $msbuild (Join-Path $PSScriptRoot 'test\TortoiseSCM\ShellTests.vcxproj') /nologo /verbosity:minimal "/p:Configuration=$Configuration" /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Shell test compilation failed.' }
    & (Join-Path $out 'ShellTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Shell tests failed.' }
    & (Join-Path $out 'ShellTests.exe') --modern-dll $out
    if ($LASTEXITCODE -ne 0) { throw 'Modern shell production DLL tests failed.' }
    $uiSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM') -Filter '*.cs' | ForEach-Object FullName)
    $uiSources += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\UiTests.cs'
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\RevisionGraphUiTests.cs'
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\TextEditorUiTests.cs'
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\TextMergePlanUiTests.cs'
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\BeyondCompareSettingsUiTests.cs'
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\HistoricalBeyondCompareUiTests.cs'
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\CheckinUiTests.cs'
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\BranchRenameUiTests.cs'
    $uiOutput = Join-Path $out 'UiTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /main:TortoiseSCM.UiTests /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/out:$uiOutput" $uiSources
    if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
    $artifacts = Join-Path $PSScriptRoot "bin\TortoiseSCM\qa\$Configuration"
    if ($Workspace) { & $uiOutput $artifacts $Workspace } else { & $uiOutput $artifacts }
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
    if ($Integration) {
        $integrationOutput = Join-Path $out 'CliIntegrationTests.exe'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Web.Extensions.dll "/out:$integrationOutput" (Join-Path $PSScriptRoot 'test\TortoiseSCM\CliIntegrationTests.cs')
        if ($LASTEXITCODE -ne 0) { throw 'CLI integration test compilation failed.' }
        $setupArguments = @{}
        if ($Workspace) { $setupArguments.ReferenceWorkspace = $Workspace }
        $manifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $manifest -or -not (Test-Path -LiteralPath $manifest)) { throw 'Test workspace setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-integration.txt'), $manifest)
        & $integrationOutput (Join-Path $out 'TortoiseSCM.exe') $manifest
        if ($LASTEXITCODE -ne 0) { throw "Server-backed CLI tests failed. Inspect $manifest and cli-integration-results.json beside it." }
        $builtInOutput = Join-Path $out 'BuiltInToolsIntegrationTests.exe'
        $builtInSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
        $builtInSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\BuiltInToolsIntegrationTests.cs'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$builtInOutput" $builtInSources
        if ($LASTEXITCODE -ne 0) { throw 'Built-in tools integration test compilation failed.' }
        $builtInManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $builtInManifest -or -not (Test-Path -LiteralPath $builtInManifest)) { throw 'Built-in tools fixture setup failed.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-builtin-tools.txt'), $builtInManifest)
        & $builtInOutput $builtInManifest 'D:\Program Files\PlasticSCM5\client\cm.exe'
        if ($LASTEXITCODE -ne 0) { throw "Built-in tools integration tests failed. Inspect $builtInManifest." }
        $bcManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $bcManifest -or -not (Test-Path -LiteralPath $bcManifest)) { throw 'Beyond Compare contract fixture setup failed.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-beyond-compare.txt'), $bcManifest)
        & $builtInOutput $bcManifest 'D:\Program Files\PlasticSCM5\client\cm.exe' --bc-contract
        if ($LASTEXITCODE -ne 0) { throw "Beyond Compare contract integration tests failed. Inspect $bcManifest." }
        $bcBrowsingOutput = Join-Path $out 'BeyondCompareBrowsingIntegrationTests.exe'
        $bcBrowsingSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
        $bcBrowsingSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\BeyondCompareBrowsingIntegrationTests.cs'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$bcBrowsingOutput" $bcBrowsingSources
        if ($LASTEXITCODE -ne 0) { throw 'Beyond Compare browsing integration test compilation failed.' }
        $bcBrowsingManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $bcBrowsingManifest -or -not (Test-Path -LiteralPath $bcBrowsingManifest)) { throw 'Beyond Compare browsing fixture setup failed.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-bc-browsing.txt'), $bcBrowsingManifest)
        & $bcBrowsingOutput $bcBrowsingManifest 'D:\Program Files\PlasticSCM5\client\cm.exe'
        if ($LASTEXITCODE -ne 0) { throw "Beyond Compare browsing integration tests failed. Inspect $bcBrowsingManifest." }
        $bcWorkingOutput = Join-Path $out 'WorkingBeyondCompareIntegrationTests.exe'
        $bcWorkingSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
        $bcWorkingSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\WorkingBeyondCompareIntegrationTests.cs'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$bcWorkingOutput" $bcWorkingSources
        if ($LASTEXITCODE -ne 0) { throw 'Working-file Beyond Compare integration compilation failed.' }
        $bcWorkingManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $bcWorkingManifest -or -not (Test-Path -LiteralPath $bcWorkingManifest)) { throw 'Working-file Beyond Compare fixture setup failed.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-bc-working.txt'), $bcWorkingManifest)
        & $bcWorkingOutput $bcWorkingManifest 'D:\Program Files\PlasticSCM5\client\cm.exe'
        if ($LASTEXITCODE -ne 0) { throw "Working-file Beyond Compare integration failed. Inspect $bcWorkingManifest." }
        $bcWorkingUiOutput = Join-Path $out 'WorkingBeyondCompareUiTests.exe'
        $bcWorkingUiSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM') -Filter '*.cs' | ForEach-Object FullName)
        $bcWorkingUiSources += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
        $bcWorkingUiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\WorkingBeyondCompareUiTests.cs'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /main:TortoiseSCM.WorkingBeyondCompareUiTests /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/out:$bcWorkingUiOutput" $bcWorkingUiSources
        if ($LASTEXITCODE -ne 0) { throw 'Working-file Beyond Compare UI compilation failed.' }
        $bcWorkingFixture = Get-Content -LiteralPath $bcWorkingManifest -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($role in @('producer', 'partial')) {
            & $bcWorkingUiOutput (Join-Path $bcWorkingFixture.runDirectory "ui-$role") $bcWorkingFixture.$role
            if ($LASTEXITCODE -ne 0) { throw "Working-file Beyond Compare UI failed for $role. Inspect $bcWorkingManifest." }
        }
        $checkinOutput = Join-Path $out 'CheckinPreflightIntegrationTests.exe'
        $checkinSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
        $checkinSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\CheckinPreflightIntegrationTests.cs'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$checkinOutput" $checkinSources
        if ($LASTEXITCODE -ne 0) { throw 'Checkin preflight integration compilation failed.' }
        $checkinManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $checkinManifest -or -not (Test-Path -LiteralPath $checkinManifest)) { throw 'Checkin preflight fixture setup failed.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-checkin-preflight.txt'), $checkinManifest)
        & $checkinOutput $checkinManifest 'D:\Program Files\PlasticSCM5\client\cm.exe'
        if ($LASTEXITCODE -ne 0) { throw "Checkin preflight integration failed. Inspect $checkinManifest." }
        $comparisonManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $comparisonManifest -or -not (Test-Path -LiteralPath $comparisonManifest)) { throw 'Changeset comparison setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-comparison.txt'), $comparisonManifest)
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\ChangesetComparisonIntegrationTests.ps1') -Manifest $comparisonManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        $branchManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $branchManifest -or -not (Test-Path -LiteralPath $branchManifest)) { throw 'Branch setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-branches.txt'), $branchManifest)
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\BranchIntegrationTests.ps1') -Manifest $branchManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        $branchCreationManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $branchCreationManifest -or -not (Test-Path -LiteralPath $branchCreationManifest)) { throw 'Branch creation setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-branch-create.txt'), $branchCreationManifest)
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\BranchCreationIntegrationTests.ps1') -Manifest $branchCreationManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        $branchRenameManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $branchRenameManifest -or -not (Test-Path -LiteralPath $branchRenameManifest)) { throw 'Branch rename fixture setup failed.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-branch-rename.txt'), $branchRenameManifest)
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\BranchRenameIntegrationTests.ps1') -Manifest $branchRenameManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\BranchHierarchyIntegrationTests.ps1') -Manifest $branchCreationManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        $shelvesManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $shelvesManifest -or -not (Test-Path -LiteralPath $shelvesManifest)) { throw 'Shelveset setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-shelves-cli.txt'), $shelvesManifest)
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\ShelvesIntegrationTests.ps1') -Manifest $shelvesManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\ShelveContentIntegrationTests.ps1') -Manifest $shelvesManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\RepositoryBrowserIntegrationTests.ps1') -Manifest $shelvesManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\LabelIntegrationTests.ps1') -Manifest $shelvesManifest -Executable (Join-Path $out 'TortoiseSCM.exe') -UiExecutable $uiOutput
        $mergeIntegrationOutput = Join-Path $out 'MergeIntegrationTests.exe'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$mergeIntegrationOutput" (Join-Path $PSScriptRoot 'test\TortoiseSCM\MergeIntegrationTests.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Merge integration test compilation failed.' }
        $mergeManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $mergeManifest -or -not (Test-Path -LiteralPath $mergeManifest)) { throw 'Merge workspace setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-merge-integration.txt'), $mergeManifest)
        & $mergeIntegrationOutput (Join-Path $out 'TortoiseSCM.exe') $mergeManifest
        if ($LASTEXITCODE -ne 0) { throw "Server-backed merge tests failed. Inspect $mergeManifest and merge-integration-results.json beside it." }
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\RevisionGraphIntegrationTests.ps1') -Manifest $shelvesManifest -Executable (Join-Path $out 'TortoiseSCM.exe') -UiExecutable $uiOutput
        $partialOutput = Join-Path $out 'PartialConflictIntegrationTests.exe'
        $partialSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
        $partialSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\PartialConflictIntegrationTests.cs'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$partialOutput" $partialSources
        if ($LASTEXITCODE -ne 0) { throw 'Partial conflict integration test compilation failed.' }
        $partialManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $partialManifest -or -not (Test-Path -LiteralPath $partialManifest)) { throw 'Partial conflict setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-partial-integration.txt'), $partialManifest)
        & $partialOutput $partialManifest 'D:\Program Files\PlasticSCM5\client\cm.exe'
        if ($LASTEXITCODE -ne 0) { throw "Partial conflict tests failed. Inspect $partialManifest and partial-conflict-results.json beside it." }
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\PartialConflictCliTests.ps1') -Manifest $partialManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        $structureOutput = Join-Path $out 'PartialStructureIntegrationTests.exe'
        $structureSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
        $structureSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\PartialStructureIntegrationTests.cs'
        & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$structureOutput" $structureSources
        if ($LASTEXITCODE -ne 0) { throw 'Partial structure integration test compilation failed.' }
        $structureManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $structureManifest -or -not (Test-Path -LiteralPath $structureManifest)) { throw 'Partial structure setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-structure-integration.txt'), $structureManifest)
        & $structureOutput $structureManifest 'D:\Program Files\PlasticSCM5\client\cm.exe'
        if ($LASTEXITCODE -ne 0) { throw "Partial structure tests failed. Inspect $structureManifest." }
        foreach ($moveSuite in @('PartialCrossDirectoryTests', 'PartialIncomingMoveTests', 'PartialMoveCollisionTests', 'PartialDirectoryIntegrationTests', 'PartialDirectoryRepeatedMoveTests', 'PartialDeletedIdentityTests', 'PartialDirectoryKeepDeletedTests', 'PartialDirectoryReaddCollisionTests')) {
            $moveOutput = Join-Path $out ($moveSuite + '.exe')
            $moveSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
            $moveSources += Join-Path $PSScriptRoot ('test\TortoiseSCM\' + $moveSuite + '.cs')
            & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$moveOutput" $moveSources
            if ($LASTEXITCODE -ne 0) { throw "$moveSuite compilation failed." }
            $moveManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
            if (-not $moveManifest -or -not (Test-Path -LiteralPath $moveManifest)) { throw "$moveSuite setup failed." }
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot ('bin\TortoiseSCM\qa\latest-' + $moveSuite + '.txt')), $moveManifest)
            if ($moveSuite -eq 'PartialDeletedIdentityTests' -or $moveSuite -eq 'PartialDirectoryReaddCollisionTests') {
                & $moveOutput $moveManifest 'D:\Program Files\PlasticSCM5\client\cm.exe' (Join-Path $out 'TortoiseSCM.exe')
            } else {
                & $moveOutput $moveManifest 'D:\Program Files\PlasticSCM5\client\cm.exe'
            }
            if ($LASTEXITCODE -ne 0) { throw "$moveSuite failed. Inspect $moveManifest." }
            if ($moveSuite -eq 'PartialDirectoryIntegrationTests' -or $moveSuite -eq 'PartialDirectoryKeepDeletedTests') {
                $fullManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
                if (-not $fullManifest -or -not (Test-Path -LiteralPath $fullManifest)) { throw 'Full-mode directory setup failed.' }
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot ('bin\TortoiseSCM\qa\latest-' + $moveSuite + '-full.txt')), $fullManifest)
                & $moveOutput $fullManifest 'D:\Program Files\PlasticSCM5\client\cm.exe' '--full'
                if ($LASTEXITCODE -ne 0) { throw "Full-mode directory tests failed. Inspect $fullManifest." }
            }
        }
        $structureCliManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $structureCliManifest -or -not (Test-Path -LiteralPath $structureCliManifest)) { throw 'Partial structure CLI setup did not produce a manifest.' }
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\PartialStructureCliTests.ps1') -Manifest $structureCliManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        $directoryCliManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $directoryCliManifest -or -not (Test-Path -LiteralPath $directoryCliManifest)) { throw 'Partial directory CLI setup did not produce a manifest.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-partial-directory-cli.txt'), $directoryCliManifest)
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\PartialDirectoryCliTests.ps1') -Manifest $directoryCliManifest -Executable (Join-Path $out 'TortoiseSCM.exe')
        $directoryFullCliManifest = & (Join-Path $PSScriptRoot 'test\TortoiseSCM\New-TestWorkspace.ps1') @setupArguments
        if (-not $directoryFullCliManifest -or -not (Test-Path -LiteralPath $directoryFullCliManifest)) { throw 'Full-mode Partial directory CLI setup failed.' }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'bin\TortoiseSCM\qa\latest-partial-directory-full-cli.txt'), $directoryFullCliManifest)
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\PartialDirectoryCliTests.ps1') -Manifest $directoryFullCliManifest -Executable (Join-Path $out 'TortoiseSCM.exe') -FullWorkspace
        & (Join-Path $PSScriptRoot 'test\TortoiseSCM\Invoke-DirectoryMatrixTests.ps1')
    }
}
Write-Host "Built: $out\TortoiseSCM.exe"
Write-Host 'Explorer registration: .\contrib\tortoisescm\Register-Shell.ps1'
