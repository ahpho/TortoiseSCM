# Creates isolated, empty server-backed workspaces for integration testing.
# This intentionally leaves its branch/workspaces in place for inspection.
[CmdletBinding()]
param(
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [string]$ReferenceWorkspace = ''
)

$ErrorActionPreference = 'Stop'
$repo = $null
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($ReferenceWorkspace)) {
    $ReferenceWorkspace = Join-Path (Split-Path -Parent $sourceRoot) 'TestSCM'
}
$ReferenceWorkspace = [IO.Path]::GetFullPath($ReferenceWorkspace)
$qaRoot = Join-Path $sourceRoot 'bin\TortoiseSCM\qa'
$runId = 'integration-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $qaRoot $runId
$branch = '/main/tortoisescm-autotest-' + $runId
$manifestPath = Join-Path $runRoot 'manifest.json'
$utf8 = New-Object Text.UTF8Encoding($false)
New-Item -ItemType Directory -Path $runRoot | Out-Null
$manifest = [ordered]@{
    runId = $runId
    repository = $repo
    branch = $branch
    baseChangeset = 0
    createdAt = (Get-Date -Format o)
    referenceWorkspace = $ReferenceWorkspace
    runDirectory = $runRoot
    producer = (Join-Path $runRoot 'producer')
    consumer = (Join-Path $runRoot 'consumer')
    partial = (Join-Path $runRoot 'partial')
    workspaceNames = @()
    partialReady = $false
    workspaces = @()
    commands = @()
    complete = $false
}

function Save-Manifest {
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), $utf8)
}

function Invoke-Cm([string[]]$CmArguments, [string]$WorkingDirectory) {
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $savedErrorAction = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $lines = @(& $CmPath @CmArguments 2>&1)
        $code = $LASTEXITCODE
        $ErrorActionPreference = $savedErrorAction
        $output = ($lines | ForEach-Object { "$_" }) -join "`n"
        $manifest.commands += [ordered]@{ cwd = $WorkingDirectory; arguments = $CmArguments; exitCode = $code; output = $output }
        Save-Manifest
        if ($code -ne 0) { throw "cm $($CmArguments -join ' ') exited $code`: $output" }
        return $output
    }
    finally { Pop-Location }
}

# Read-only baseline. No modifying command ever runs in ReferenceWorkspace.
$manifest.referenceSelectorBefore = Invoke-Cm @('showselector') $ReferenceWorkspace
$repositoryMatches = [regex]::Matches($manifest.referenceSelectorBefore, '(?m)^\s*repository\s+"(?<spec>[^"\r\n]+)"\s*$')
if ($repositoryMatches.Count -ne 1) {
    throw 'Reference workspace must select exactly one repository.'
}
$repo = $repositoryMatches[0].Groups['spec'].Value
if ($repo -cnotmatch '^TestSCM@[^\s"\r\n]+$') {
    throw 'Reference workspace does not select the explicitly permitted TestSCM repository.'
}
$manifest.repository = $repo
Save-Manifest
$manifest.referenceStatusBefore = Invoke-Cm @('status', '--short', '--machinereadable') $ReferenceWorkspace
$baseTree = Invoke-Cm @('ls', "--tree=cs:0@$repo", '-R', '--format={size}|{path}') $ReferenceWorkspace
if ($baseTree.Trim() -ne '0|/') { throw "Changeset 0 is not empty: $baseTree" }

Invoke-Cm @('branch', 'create', "br:$branch@$repo", "--changeset=cs:0@$repo", '-c=TortoiseSCM isolated automated integration test') $runRoot | Out-Null
foreach ($role in @('producer', 'consumer', 'partial')) {
    $path = Join-Path $runRoot $role
    $name = 'tscm-' + $runId + '-' + $role
    $manifest.workspaceNames += $name
    New-Item -ItemType Directory -Path $path | Out-Null
    $workspace = [ordered]@{ role = $role; name = $name; path = $path; ready = $false }
    $manifest.workspaces += $workspace
    Save-Manifest
    Invoke-Cm @('workspace', 'create', $name, $path, $repo) $runRoot | Out-Null
    # switch downloads only the verified empty cs:0 tree, never /main head.
    Invoke-Cm @('switch', "br:$branch@$repo", "--workspace=$path") $runRoot | Out-Null
    if ($role -eq 'partial') {
        Invoke-Cm @('partial', 'configure', '-/', '+/') $path | Out-Null
        # An empty configure can retain the complete tree. This forces partial
        # tree semantics (cs:-1); plastic.workspace may still say Standard.
        Invoke-Cm @('partial', 'update', '.', '--report') $path | Out-Null
    }
    $workspace.selector = Invoke-Cm @('showselector') $path
    $workspace.status = Invoke-Cm @('status', '--header') $path
    $workspace.metadataFiles = @(Get-ChildItem -LiteralPath (Join-Path $path '.plastic') -File -Force | Select-Object -ExpandProperty Name)
    $workspace.metadata = [IO.File]::ReadAllText((Join-Path $path '.plastic\plastic.workspace'))
    $workspace.isPartialTree = $workspace.status.Contains('(cs:-1 ')
    if (($role -eq 'partial') -ne $workspace.isPartialTree) { throw "Unexpected workspace tree type for $role" }
    $workspace.ready = $true
    if ($role -eq 'partial') { $manifest.partialReady = $true }
    Save-Manifest
}
$manifest.referenceSelectorAfter = Invoke-Cm @('showselector') $ReferenceWorkspace
$manifest.referenceStatusAfter = Invoke-Cm @('status', '--short', '--machinereadable') $ReferenceWorkspace
if ($manifest.referenceSelectorBefore -ne $manifest.referenceSelectorAfter -or $manifest.referenceStatusBefore -ne $manifest.referenceStatusAfter) {
    throw 'Reference workspace baseline changed during setup; inspect manifest.'
}
$manifest.complete = $true
Save-Manifest
Write-Output $manifestPath
