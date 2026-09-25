# GPL-2.0-or-later. Native command research; only a fresh isolated fixture is accepted.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ManifestPath,
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [ValidateSet('exact','configure','unload','parent','follow')][string]$Method = 'unload', [switch]$FullyLoaded, [switch]$ExactFiles)
$ErrorActionPreference = 'Stop'
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $ManifestPath
$run = $m.runDirectory
$evidence = [Collections.Generic.List[object]]::new()
$resultFile = Join-Path $run 'incoming-move-native-probe.json'
$utf8 = New-Object Text.UTF8Encoding($false)
function Save { [IO.File]::WriteAllText($resultFile, ($evidence.ToArray() | ConvertTo-Json -Depth 20), $utf8) }
function Native([string]$cwd, [string[]]$arguments, [switch]$AllowFailure) {
    if (![IO.Path]::GetFullPath($cwd).StartsWith($run + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Native probe cwd is outside fixture' }
    Push-Location -LiteralPath $cwd
    try {
        $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $lines = @(& $CmPath @arguments 2>&1); $code = $LASTEXITCODE
        $ErrorActionPreference = $saved
        $output = ($lines | ForEach-Object { "$_" }) -join "`n"
        $evidence.Add([ordered]@{cwd=$cwd; arguments=$arguments; exitCode=$code; output=$output}); Save
        if ($code -ne 0 -and !$AllowFailure) { throw "Native command failed: $($arguments -join ' '): $output" }
        return $output
    } finally { Pop-Location }
}
function WriteFile([string]$path, [string]$content) { [IO.File]::WriteAllText($path, $content, $utf8) }
function Snapshot([string]$label) {
    $files = @(Get-ChildItem -LiteralPath $m.partial -File -Recurse -Force | Where-Object { !$_.FullName.StartsWith((Join-Path $m.partial '.plastic') + '\') } | ForEach-Object { [ordered]@{path=$_.FullName.Substring($m.partial.Length); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; text=[IO.File]::ReadAllText($_.FullName)} })
    $evidence.Add([ordered]@{label=$label; files=$files; selector=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector')); loadRules=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.fullycheckeddirectories'))}); Save
    Native $m.partial @('status','--short','--machinereadable') | Out-Null
}
if (Test-Path -LiteralPath $resultFile) { throw 'Do not rerun probe in an existing fixture' }
if ((Get-ChildItem -LiteralPath $m.producer -Force | Where-Object { $_.Name -ne '.plastic' }).Count) { throw 'Probe requires an empty fresh producer' }
Native $m.partial @('partial','update','--help') | Out-Null
Native $m.partial @('partial','configure','--help') | Out-Null
# One method per fixture: configure +new alone can duplicate an existing ItemId
# in the local tree and must not contaminate another method's experiment.
$methods = @($Method)
foreach ($kind in @('same','cross')) { foreach ($method in $methods) {
    $case = "$kind-$method"; $dir = Join-Path $m.producer $case
    New-Item -ItemType Directory -Path $dir | Out-Null
    if ($kind -eq 'cross') { New-Item -ItemType Directory -Path (Join-Path $dir 'destination') | Out-Null }
    WriteFile (Join-Path $dir 'original.txt') "base $case"
    WriteFile (Join-Path $dir 'loaded-sibling.txt') "base sibling $case"
    WriteFile (Join-Path $dir 'unloaded-sibling.txt') "unloaded sibling $case"
}}
WriteFile (Join-Path $m.producer 'sentinel.txt') 'base sentinel'
Native $m.producer @('add',$m.producer,'-R') | Out-Null
Native $m.producer @('checkin',$m.producer,'--all','-c=Incoming move native probe base') | Out-Null
Native $m.partial @('partial','update',$m.partial,'--dontmerge','--report') | Out-Null
if ($FullyLoaded) { Native $m.partial @('partial','configure','+/','--restorefulldirs') | Out-Null }
if ($ExactFiles) {
    Native $m.partial @('partial','configure','-/') | Out-Null
    foreach ($kind in @('same','cross')) { foreach ($method in $methods) {
        $case = "$kind-$method"
        Native $m.partial @('partial','configure',"+/$case/original.txt", "+/$case/loaded-sibling.txt") | Out-Null
        if ($kind -eq 'cross') { Native $m.partial @('partial','configure',"+/$case/destination") | Out-Null }
    }}
    Native $m.partial @('partial','configure','+/sentinel.txt') | Out-Null
}
foreach ($kind in @('same','cross')) { foreach ($method in $methods) {
    $case = "$kind-$method"
    if (!$FullyLoaded -and !$ExactFiles) { Native $m.partial @('partial','configure',"-/$case/unloaded-sibling.txt") | Out-Null }
    WriteFile (Join-Path $m.partial "$case/original.txt") "local edit $case"
    $dest = if ($kind -eq 'same') { "$case/moved.txt" } else { "$case/destination/moved.txt" }
    Native $m.producer @('move',(Join-Path $m.producer "$case/original.txt"),(Join-Path $m.producer $dest)) | Out-Null
    WriteFile (Join-Path $m.producer $dest) "incoming edit $case"
    WriteFile (Join-Path $m.producer "$case/loaded-sibling.txt") "incoming sibling $case"
}}
foreach ($kind in @('same','cross')) { foreach ($method in $methods) {
    $case = "$kind-$method"
    Native $m.producer @('checkin',(Join-Path $m.producer $case),'--all',"-c=Incoming move native probe $case") | Out-Null
}}
WriteFile (Join-Path $m.partial 'sentinel.txt') 'unrelated pending sentinel'
$head = (Native $m.producer @('find','changeset',("where branch = '" + $m.branch + "' order by changesetid desc limit 1"),'--format={changesetid}','--nototal')).Trim()
if ($head -notmatch '^[0-9]+$') { throw 'Cannot pin fixture head' }
$selectorBefore = [IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector'))
$rulesBefore = @(Get-Content -Encoding UTF8 (Join-Path $m.partial '.plastic/plastic.fullycheckeddirectories') | Sort-Object) -join "`n"
Snapshot 'Before decisions'
foreach ($kind in @('same','cross')) { foreach ($method in $methods) {
    $case = "$kind-$method"; $original = Join-Path $m.partial "$case/original.txt"
    $destRepo = if ($kind -eq 'same') { "/$case/moved.txt" } else { "/$case/destination/moved.txt" }
    $dest = Join-Path $m.partial $destRepo.TrimStart('/')
    [xml]$beforeIdentity = Native $m.partial @('ls',$original,'--xml','--encoding=utf-8')
    $originalId = [string]$beforeIdentity.LsResults.LsItems.LsItem.ItemId
    Copy-Item -LiteralPath $original -Destination (Join-Path $run "$case-local-backup.txt")
    Native $m.partial @('partial','undo',$original) | Out-Null
    switch ($method) {
        'exact' { Native $m.partial @('partial','update',$original,$dest,'--dontmerge','--report') -AllowFailure | Out-Null }
        'configure' { Native $m.partial @('partial','configure',('+' + $destRepo)) -AllowFailure | Out-Null }
        'unload' {
            Native $m.partial @('partial','configure',("-/$case/original.txt")) -AllowFailure | Out-Null
            Native $m.partial @('partial','configure',('+' + $destRepo)) -AllowFailure | Out-Null
        }
        'follow' {
            Native $m.partial @('partial','move',$original,$dest) -AllowFailure | Out-Null
            Native $m.partial @('partial','update',$dest,'--dontmerge','--report') -AllowFailure | Out-Null
        }
        'parent' { Native $m.partial @('partial','update',$original,(Split-Path -Parent $dest),'--dontmerge','--report') -AllowFailure | Out-Null }
    }
    if ($method -eq 'unload') {
        Native $m.partial @('partial','update',$dest,"--changeset=$head",'--dontmerge','--report') | Out-Null
        [xml]$afterIdentity = Native $m.partial @('ls',$dest,'--xml','--encoding=utf-8')
        [xml]$incomingIdentity = Native $m.partial @('ls',$destRepo,("--tree=cs:" + $head + '@' + $m.repository),'--xml','--encoding=utf-8')
        if ($originalId -ne [string]$afterIdentity.LsResults.LsItems.LsItem.ItemId -or $originalId -ne [string]$incomingIdentity.LsResults.LsItems.LsItem.ItemId) { throw "Item identity changed: $case" }
        $rulesAfter = @(Get-Content -Encoding UTF8 (Join-Path $m.partial '.plastic/plastic.fullycheckeddirectories') | Sort-Object) -join "`n"
        $status = Native $m.partial @('status','--short','--machinereadable')
        if ((Test-Path -LiteralPath $original) -or [IO.File]::ReadAllText($dest) -ne "incoming edit $case" -or
            [IO.File]::ReadAllText((Join-Path $m.partial "$case/loaded-sibling.txt")) -ne "base sibling $case" -or
            ((Test-Path -LiteralPath (Join-Path $m.partial "$case/unloaded-sibling.txt")) -ne [bool]$FullyLoaded) -or
            [IO.File]::ReadAllText((Join-Path $m.partial 'sentinel.txt')) -ne 'unrelated pending sentinel' -or
            [IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector')) -ne $selectorBefore -or
            $rulesAfter -ne $rulesBefore -or $status.Contains("\$case\")) { throw "Unload-load invariant failed: $case" }
        $evidence.Add([ordered]@{label="PASS $case"; itemId=$originalId; pinnedChangeset=$head; siblingBytesPreserved=$true; unloadedSiblingAbsent=(!$FullyLoaded); selectorPreserved=$true; loadRuleSetPreserved=$true; noMovePending=$true}); Save
        # Retain the local edit under the moved identity without publishing it.
        Copy-Item -LiteralPath (Join-Path $run "$case-local-backup.txt") -Destination $dest -Force
        Native $m.partial @('status','--short','--machinereadable') | Out-Null
    }
    Snapshot "After $case"
}}
Write-Output $resultFile
