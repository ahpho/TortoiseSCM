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
    $cliOutput = Join-Path $out 'CliTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll "/out:$cliOutput" (Join-Path $PSScriptRoot 'test\TortoiseSCM\CliTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'CLI test compilation failed.' }
    & $cliOutput (Join-Path $out 'TortoiseSCM.exe')
    if ($LASTEXITCODE -ne 0) { throw 'CLI black-box tests failed.' }
    & $msbuild (Join-Path $PSScriptRoot 'test\TortoiseSCM\ShellTests.vcxproj') /nologo /verbosity:minimal "/p:Configuration=$Configuration" /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Shell test compilation failed.' }
    & (Join-Path $out 'ShellTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Shell tests failed.' }
    $uiSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM') -Filter '*.cs' | ForEach-Object FullName)
    $uiSources += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    $uiSources += Join-Path $PSScriptRoot 'test\TortoiseSCM\UiTests.cs'
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
    }
}
Write-Host "Built: $out\TortoiseSCM.exe"
Write-Host 'Explorer registration: .\contrib\tortoisescm\Register-Shell.ps1'
