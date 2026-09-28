# Reproducible real GUI workflows. Server writes stay on separate autotest branches.
[CmdletBinding()]
param(
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [string]$ReferenceWorkspace,
    [string]$BeyondComparePath,
    # Supplementary checkout/row-undo/rename/ignore scenarios, each on a fresh fixture.
    [switch]$FileActionsOnly
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifacts = Join-Path $repo ('bin\TortoiseSCM\qa\basic-gui-acceptance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TortoiseSCM') -Filter '*.cs' | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
$setup = @{ CmPath = $CmPath }
if ($ReferenceWorkspace) { $setup.ReferenceWorkspace = $ReferenceWorkspace }
$results = @()
$suites = if ($FileActionsOnly) { @('BasicWorkflowGuiIntegrationTests') } else {
    @('BasicWorkflowGuiIntegrationTests', 'HistoryWorkflowGuiIntegrationTests', 'UpdateWorkflowGuiIntegrationTests')
}
foreach ($suite in $suites) {
    $main = if ($suite -eq 'BasicWorkflowGuiIntegrationTests') { 'TortoiseSCM.' + $suite } else { $suite }
    $output = Join-Path $artifacts ($suite + '.exe')
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 "/main:$main" "/win32icon:$(Join-Path $repo 'src\Resources\gluon.ico')" /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/out:$output" ($sources + (Join-Path $PSScriptRoot ($suite + '.cs')))
    if ($LASTEXITCODE -ne 0) { throw "$suite compilation failed." }
    $manifest = & (Join-Path $PSScriptRoot 'New-TestWorkspace.ps1') @setup
    if (-not $manifest -or -not (Test-Path -LiteralPath $manifest)) { throw 'Fixture creation failed.' }
    $second = if ($suite -eq 'BasicWorkflowGuiIntegrationTests') { Join-Path $artifacts $suite } else { $CmPath }
    # Explicit argv quoting. Neither path can contain a literal quote on Windows.
    $arguments = '"' + $manifest + '" "' + $second + '"'
    if ($suite -eq 'BasicWorkflowGuiIntegrationTests') { $arguments += ' "' + $CmPath + '"' }
    if ($FileActionsOnly) { $arguments += ' --file-actions' }
    $process = Start-Process -FilePath $output -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait `
        -RedirectStandardOutput (Join-Path $artifacts ($suite + '.log')) -RedirectStandardError (Join-Path $artifacts ($suite + '-error.log'))
    $results += [pscustomobject]@{ suite = $suite; fileActionsOnly = [bool]$FileActionsOnly; manifest = $manifest; exitCode = $process.ExitCode }
    [IO.File]::WriteAllText((Join-Path $artifacts 'results.json'), ($results | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
    if ($process.ExitCode -ne 0) { throw "$suite failed. Inspect $artifacts and $manifest." }
    Get-Content -LiteralPath (Join-Path $artifacts ($suite + '.log')) -Tail 1
}
if ($BeyondComparePath) {
    $output = Join-Path $artifacts 'BeyondCompareGuiSmokeTests.exe'
    $core = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\TortoiseSCM\Core') -Filter '*.cs' | ForEach-Object FullName)
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$output" ($core + (Join-Path $PSScriptRoot 'BeyondCompareGuiSmokeTests.cs'))
    if ($LASTEXITCODE -ne 0) { throw 'BC smoke compilation failed.' }
    $arguments = '"' + $BeyondComparePath + '" "' + (Join-Path $artifacts 'real-bc') + '"'
    $process = Start-Process -FilePath $output -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait `
        -RedirectStandardOutput (Join-Path $artifacts 'real-bc.log') -RedirectStandardError (Join-Path $artifacts 'real-bc-error.log')
    if ($process.ExitCode -ne 0) { throw "Real BC smoke failed. Inspect $artifacts." }
}
Write-Output "GUI acceptance artifacts: $artifacts"
