# GPL-2.0-or-later. Read-only native graph comparisons in isolated workspaces.
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release/TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [string]$UiExecutable
)
$ErrorActionPreference = 'Stop'
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $Manifest
$utf8 = New-Object Text.UTF8Encoding($false)
$events = New-Object 'Collections.Generic.List[object]'
$assertions = 0
function Assert([bool]$condition, [string]$message) {
    $script:assertions++; $events.Add([pscustomobject]@{check=$message;passed=$condition})
    if (!$condition) { throw $message }; Write-Host "PASS: $message"
}
function Quote([string]$value) { '"' + [regex]::Replace([regex]::Replace($value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }
function Run([string]$file, [string]$workspace, [string[]]$arguments, [int]$expected=0) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=[IO.Path]::GetFullPath($file); $start.WorkingDirectory=$workspace
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $start.Arguments=($arguments | ForEach-Object { Quote $_ }) -join ' '
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'Subprocess timed out; inspect the isolated fixture before retrying.' }
        $text=$stdout.GetAwaiter().GetResult(); $errorText=$stderr.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{command=$arguments;workspace=$workspace;exit=$process.ExitCode;output=$text;error=$errorText})
        if ($process.ExitCode -ne $expected) { throw "Exit $($process.ExitCode), expected $expected`: $text $errorText" }
        return $text
    } finally { $process.Dispose() }
}

function Invoke-GraphCli([string]$command, [string]$workspace, [string[]]$extra=@(), [int]$expected=0) {
    Run $Executable $workspace (@('--cli','--json','--cm',$CmPath,'--command',$command,'--path',$workspace)+$extra) $expected | ConvertFrom-Json
}
function StableStatus([string]$workspace) {
    # cm status does not guarantee row order, especially for Partial workspaces.
    (([string](Run $CmPath $workspace @('status','--short','--machinereadable')) -split '\r?\n' | Where-Object { $_.Length -gt 0 } | Sort-Object) -join "`n")
}
function Snapshot([string]$workspace) {
    $metadata=(Join-Path $workspace '.plastic')+'\'
    (@(Get-ChildItem -LiteralPath $workspace -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($workspace.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
function NativeXml([string]$kind,[string]$query) {
    [xml](Run $CmPath $m.consumer @('find',$kind,($query+" on repository '"+$m.repository+"'"),'--xml','--encoding=utf-8','--nototal'))
}
function EdgeKey($edge) {
    ([string]$edge.id)+'|'+$edge.kind+'|'+$edge.sourceChangeset+'|'+$edge.destinationChangeset+'|'+([string]$edge.baseChangeset)
}
function CheckPage($page,[long]$before,[int]$limit) {
    Assert ($page.repository -ceq $m.repository -and @($page.nodes).Count -le $limit) 'Graph reports captured repository and bounded node count'
    $native=NativeXml 'changeset' ("where changesetid < $before order by changesetid desc limit "+($limit+1))
    $candidates=@($native.PLASTICQUERY.CHANGESET | Where-Object { $_ } | Sort-Object { [long]$_.CHANGESETID } -Descending)
    $expected=@($candidates | Select-Object -First $limit)
    Assert ((@($page.nodes | ForEach-Object changeset) -join ',') -ceq (@($expected | ForEach-Object CHANGESETID) -join ',')) 'Page node identities match native descending cursor query exactly'
    Assert ($page.hasMore -eq ($candidates.Count -gt $limit)) 'More-history flag matches native sentinel row'
    if ($page.hasMore) { Assert ($page.nextBeforeChangeset -eq [long]$expected[-1].CHANGESETID) 'Continuation cursor is last returned node' }
    $known=@{}; foreach($node in $page.nodes) { $known[[string]$node.changeset]=$node }
    $expectedEdges=New-Object 'Collections.Generic.List[string]'
    foreach($row in $expected) {
        $node=$known[[string]$row.CHANGESETID]
        Assert ($node.id -eq [long]$row.ID -and $node.branch -ceq [string]$row.BRANCH -and $node.comment -ceq [string]$row.COMMENT) 'Graph node object identity, branch and comment match server'
        if ([long]$row.PARENT -eq -1) { Assert ($null -eq $node.parentChangeset) 'Native root has no parent or invented edge' }
        else {
            Assert ($node.parentChangeset -eq [long]$row.PARENT) 'Primary parent comes from exact native PARENT field'
            $expectedEdges.Add('|parent|'+$row.PARENT+'|'+$row.CHANGESETID+'|')
        }
    }
    if ($expected.Count -gt 0) {
        $condition=(@($expected | ForEach-Object { 'dstchangeset = '+$_.CHANGESETID }) -join ' or ')
        $merges=NativeXml 'merge' ("where ($condition) limit 1001")
        $links=@($merges.PLASTICQUERY.MERGE | Where-Object { $_ })
        Assert ($links.Count -le 1000) 'Native merge result stays within explicit complete-page budget'
        foreach($link in $links) { $expectedEdges.Add($link.ID+'|'+$link.TYPE+'|'+$link.SRCCHANGESET+'|'+$link.DSTCHANGESET+'|'+$link.BASECHANGESET) }
    }
    Assert ((@($page.edges | ForEach-Object { EdgeKey $_ } | Sort-Object) -join "`n") -ceq (@($expectedEdges | Sort-Object) -join "`n")) 'Every primary and typed merge edge matches native query without inferred edges'
    foreach($edge in $page.edges) {
        Assert ($edge.sourceLoaded -eq $known.ContainsKey([string]$edge.sourceChangeset)) 'Off-page source is marked explicitly'
        Assert ($edge.baseLoaded -eq ($null -ne $edge.baseChangeset -and $known.ContainsKey([string]$edge.baseChangeset))) 'Interval base visibility is accurate without adding ancestry'
    }
}
$output=Join-Path $m.runDirectory ('revision-graph-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($output) | Out-Null
$beforeState=@{}
try {
    foreach($workspace in @($m.producer,$m.consumer,$m.partial)) {
        $beforeState[$workspace]=@{status=(StableStatus $workspace);selector=[IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector'));tree=(Get-FileHash -LiteralPath (Join-Path $workspace '.plastic/plastic.wktree')).Hash;files=(Snapshot $workspace)}
    }
    $mergeXml=NativeXml 'merge' 'limit 1001'
    $merges=@($mergeXml.PLASTICQUERY.MERGE | Where-Object { $_ })
    Assert ($merges.Count -le 1000) 'Read-only fixture discovery is bounded'
    $normal=@($merges | Where-Object TYPE -ceq 'merge' | Sort-Object { [long]$_.DSTCHANGESET } | Select-Object -First 1)
    $interval=@($merges | Where-Object { $_.TYPE -like '*subtractive*' -and $_.BASECHANGESET -ne '' } | Select-Object -First 1)
    Assert ($normal.Count -eq 1 -and $interval.Count -eq 1) 'Repository contains real normal and subtractive interval merge fixtures'
    $boundaries=@(([long]$normal[0].DSTCHANGESET+1),([long]$interval[0].DSTCHANGESET+1))
    foreach($cursor in $boundaries) {
        $page=(Invoke-GraphCli 'revision-graph' $m.consumer @('--before',([string]$cursor),'--limit','20')).data
        CheckPage $page $cursor 20
        foreach($workspace in @($m.producer,$m.partial)) {
            $other=(Invoke-GraphCli 'revision-graph' $workspace @('--before',([string]$cursor),'--limit','20')).data
            Assert (($other.nodes | ConvertTo-Json -Depth 8 -Compress) -ceq ($page.nodes | ConvertTo-Json -Depth 8 -Compress) -and ($other.edges | ConvertTo-Json -Depth 8 -Compress) -ceq ($page.edges | ConvertTo-Json -Depth 8 -Compress)) 'Dirty Standard and Partial return identical server topology'
            if ($UiExecutable) { Run $UiExecutable $workspace @('--graph-live',(Join-Path $output ((Split-Path $workspace -Leaf)+'-'+$cursor)),$workspace,([string]$cursor)) | Out-Null; Assert $true 'Live native graph renders and selects fixed snapshot with unchanged workspace' }
        }
    }
    $only=(Invoke-GraphCli 'revision-graph' $m.consumer @('--before',([string]$boundaries[0]),'--limit','1')).data
    CheckPage $only $boundaries[0] 1
    Assert (@($only.edges | Where-Object { !$_.sourceLoaded }).Count -gt 0) 'Single-node graph exposes unloaded parents and merge sources'
    $older=(Invoke-GraphCli 'revision-graph' $m.consumer @('--before',([string]$only.nextBeforeChangeset),'--limit','1')).data
    Assert ($older.nodes[0].changeset -lt $only.nodes[0].changeset) 'Cursor paging progresses without repeating previous node'
    $root=(Invoke-GraphCli 'revision-graph' $m.partial @('--before','1','--limit','1')).data
    CheckPage $root 1 1
    Assert ($root.nodes[0].changeset -eq 0 -and @($root.edges).Count -eq 0 -and !$root.hasMore) 'Initial repository root has no fabricated ancestry'
    $empty=(Invoke-GraphCli 'revision-graph' $m.consumer @('--before','0')).data
    Assert (@($empty.nodes).Count -eq 0 -and @($empty.edges).Count -eq 0 -and !$empty.hasMore) 'Zero cursor returns complete empty page'
    Invoke-GraphCli 'revision-graph' $m.consumer @('--limit','101') 2 | Out-Null
    Invoke-GraphCli 'revision-graph' $m.consumer @('--yes') 2 | Out-Null
} finally {
    try {
        foreach($workspace in $beforeState.Keys) {
            Assert ((StableStatus $workspace) -ceq $beforeState[$workspace].status) 'Graph preserves pending status'
            Assert ([IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector')) -ceq $beforeState[$workspace].selector) 'Graph preserves selector'
            Assert ((Get-FileHash -LiteralPath (Join-Path $workspace '.plastic/plastic.wktree')).Hash -ceq $beforeState[$workspace].tree) 'Graph preserves loaded tree'
            Assert ((Snapshot $workspace) -ceq $beforeState[$workspace].files) 'Graph preserves every working file byte'
        }
    } finally { [IO.File]::WriteAllText((Join-Path $output 'results.json'),($events.ToArray() | ConvertTo-Json -Depth 20),$utf8) }
}
Write-Host "PASS: $assertions revision graph integration assertions; $output"