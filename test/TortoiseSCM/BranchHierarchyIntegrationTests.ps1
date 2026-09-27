# GPL-2.0-or-later. Read-only branch hierarchy checks against native metadata.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release/TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe'
)
$ErrorActionPreference='Stop'
$m=& (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $Manifest
$utf8=New-Object Text.UTF8Encoding($false)
$events=New-Object 'Collections.Generic.List[object]'
$script:assertions=0
function Assert([bool]$Condition,[string]$Description) {
    $script:assertions++; $events.Add([pscustomobject]@{description=$Description;success=$Condition})
    if (!$Condition) { throw $Description }; Write-Host "PASS: $Description"
}
function Quote([string]$Value) { return '"'+[regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"' }
function Run([string]$File,[string[]]$Arguments,[int]$Expected=0) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=[IO.Path]::GetFullPath($File); $start.WorkingDirectory=$m.producer
    $start.Arguments=($Arguments | ForEach-Object { Quote $_ }) -join ' '
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'Hierarchy test subprocess timed out' }
        $output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{file=$File;arguments=$Arguments;exitCode=$process.ExitCode;output=$output;error=$errors})
        if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode): $output $errors" }
        return $output
    } finally { $process.Dispose() }
}
function Invoke-Tree([string]$Workspace,[string[]]$Extra=@(),[int]$Expected=0) {
    $response=Run $Executable (@('--cli','--json','--command','branch-tree','--path',$Workspace,'--cm',$CmPath)+$Extra) $Expected | ConvertFrom-Json
    Assert ($response.exitCode -eq $Expected -and $response.success -eq ($Expected -eq 0)) 'Tree process and JSON result agree'
    return $response
}
function Snapshot([string]$Root) {
    $metadata=(Join-Path $Root '.plastic')+'\'
    $files=@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Root.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    foreach ($name in @('plastic.selector','plastic.workspace','plastic.wktree','plastic.fullupdate')) {
        $path=Join-Path (Join-Path $Root '.plastic') $name
        $files+=if (Test-Path -LiteralPath $path) { $name+'|'+(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { $name+'|absent' }
    }
    return $files -join "`n"
}
try {
    $before=Snapshot $m.producer; $partialBefore=Snapshot $m.partial
    [xml]$native=Run $CmPath @('find','branch','--xml','--encoding=utf-8','--nototal')
    $source=New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
    foreach ($branch in $native.PLASTICQUERY.BRANCH) { $source.Add([string]$branch.NAME,$branch) }
    $tree=Invoke-Tree $m.producer
    Assert (@($tree.data.nodes).Count -eq $source.Count) 'Hierarchy contains every visible native branch exactly once'
    $seen=New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
    $correct=$true
    foreach ($node in $tree.data.nodes) {
        if (!$source.ContainsKey($node.name) -or $seen.ContainsKey($node.name)) { $correct=$false; break }
        $original=$source[$node.name]
        if ($node.parent -cne [string]$original.PARENT -or $node.headChangeset -ne [long]$original.CHANGESET -or !$node.isMatch) { $correct=$false; break }
        $hasParent=$source.ContainsKey([string]$node.parent)
        if ($hasParent) {
            if (!$seen.ContainsKey($node.parent) -or $node.depth -ne ($seen[$node.parent].depth+1) -or $node.parentMissing) { $correct=$false; break }
        } elseif ($node.depth -ne 0 -or $node.parentMissing -ne (![string]::IsNullOrEmpty($node.parent))) { $correct=$false; break }
        $children=@($source.Values | Where-Object { [string]$_.PARENT -ceq $node.name }).Count
        if ($node.childCount -ne $children) { $correct=$false; break }
        $seen.Add($node.name,$node)
    }
    Assert $correct 'Depth, order, direct children and heads match explicit native parents'
    Assert (@($tree.data.nodes | Where-Object { $_.isCurrent -and $_.name -ceq $m.branch }).Count -eq 1) 'Standard hierarchy identifies the actual selected branch'
    $selected=@($tree.data.nodes | Where-Object { $_.name.StartsWith($m.branch+'/',[StringComparison]::Ordinal) -and $_.name.Contains('created 中文') } | Select-Object -First 1)
    Assert ($selected.Count -eq 1) 'Fixture includes a real Unicode child branch'
    $name=[string]$selected[0].name
    $filtered=Invoke-Tree $m.producer @('--filter',$name)
    $expected=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $cursor=$name
    while ($source.ContainsKey($cursor) -and $expected.Add($cursor)) { $cursor=[string]$source[$cursor].PARENT }
    Assert (@($filtered.data.nodes).Count -eq $expected.Count -and @($filtered.data.nodes | Where-Object { !$expected.Contains($_.name) }).Count -eq 0) 'Filtered hierarchy retains exactly the matching child and visible ancestors'
    Assert (@($filtered.data.nodes | Where-Object isMatch).Count -eq 1 -and @($filtered.data.nodes | Where-Object isMatch)[0].name -ceq $name) 'Ancestors are clearly separate from the actual filter match'
    Assert ($filtered.data.filter -ceq $name) 'JSON preserves reviewed Unicode filter'
    $empty=Invoke-Tree $m.producer @('--filter',('missing-'+[Guid]::NewGuid().ToString('N')))
    Assert (@($empty.data.nodes).Count -eq 0) 'Unmatched filter returns an empty hierarchy'
    $optionValue=Invoke-Tree $m.producer @('--filter','--yes')
    Assert (@($optionValue.data.nodes).Count -eq 0 -and $optionValue.data.filter -ceq '--yes') 'Option-looking filter text remains data'
    $partial=Invoke-Tree $m.partial @('--filter',$name)
    Assert (@($partial.data.nodes).Count -eq $expected.Count -and $partial.data.workspace.isPartial) 'Partial workspace reads identical branch hierarchy without loading files'
    Invoke-Tree $m.producer @('--yes') 2 | Out-Null
    Invoke-Tree $m.producer @('--branch',$name) 2 | Out-Null
    Assert ((Snapshot $m.producer) -ceq $before -and (Snapshot $m.partial) -ceq $partialBefore) 'Hierarchy reads preserve both working trees and loading metadata'
    $pending=Run $CmPath @('status','--short','--machinereadable')
    Assert ([string]::IsNullOrWhiteSpace($pending)) 'Read-only hierarchy checks leave the isolated workspace clean'
    Write-Host "PASS: $script:assertions branch hierarchy integration assertions"
} finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'branch-hierarchy-results.json'),($events.ToArray() | ConvertTo-Json -Depth 16),$utf8)
}
