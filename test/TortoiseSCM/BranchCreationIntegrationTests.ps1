# GPL-2.0-or-later. Branch creation and exact branch history on isolated fixtures.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release/TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [switch]$PrepareOnly,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
if ($PrepareOnly -and $ValidateOnly) { throw 'Choose prepare or validate, not both' }
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $Manifest
$utf8 = New-Object Text.UTF8Encoding($false)
$events = New-Object 'Collections.Generic.List[object]'
$script:assertions = 0
$fixturePath = Join-Path $m.runDirectory 'branch-creation-fixture.json'
function Assert([bool]$Condition, [string]$Description) {
    $script:assertions++; $events.Add([pscustomobject]@{description=$Description;success=$Condition})
    if (!$Condition) { throw $Description }; Write-Host "PASS: $Description"
}
function Quote([string]$Value) { return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }
function Run([string]$File, [string[]]$Arguments, [int]$Expected=0) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=[IO.Path]::GetFullPath($File); $start.WorkingDirectory=$m.producer
    $start.Arguments=($Arguments | ForEach-Object { Quote $_ }) -join ' '
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw 'Branch creation test subprocess timed out' }
        $output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{file=$File;arguments=$Arguments;exitCode=$process.ExitCode;output=$output;error=$errors})
        if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode): $output $errors" }
        return $output
    } finally { $process.Dispose() }
}
function Native([string[]]$Arguments) { Run $CmPath $Arguments }
function Invoke-BranchCli([string]$Command, [string[]]$Extra, [int]$Expected=0, [string]$Workspace=$m.producer) {
    $response=Run $Executable (@('--cli','--json','--command',$Command,'--path',$Workspace,'--cm',$CmPath)+$Extra) $Expected | ConvertFrom-Json
    Assert ($response.exitCode -eq $Expected -and $response.success -eq ($Expected -eq 0)) "CLI $Command process/JSON agree"
    return $response
}
function Head {
    $header=Native @('status','--header')
    if ($header -notmatch '\(cs:(\d+)') { throw "No changeset in status: $header" }
    return [long]$Matches[1]
}
function Write-File([string]$Relative,[string]$Content) {
    $path=Join-Path $m.producer $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path,$Content,$utf8)
}
function Snapshot([string]$Root=$m.producer) {
    $metadata=(Join-Path $Root '.plastic')+'\'
    return (@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Root.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
function PartialConfiguration {
    return (@('plastic.selector','plastic.workspace','plastic.wktree','plastic.fullupdate') | ForEach-Object {
        $file=Join-Path (Join-Path $m.partial '.plastic') $_
        if (Test-Path -LiteralPath $file) { $_+'|'+(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash } else { $_+'|absent' }
    }) -join "`n"
}
try {
    if (!$ValidateOnly) {
        if (Test-Path -LiteralPath $fixturePath) { throw 'Fixture already prepared; use -ValidateOnly' }
        if (@(Get-ChildItem -LiteralPath $m.producer -Force | Where-Object Name -ne '.plastic').Count) { throw 'Use a fresh empty fixture' }
        Write-File 'parent.txt' "base one`n"
        Native @('add','parent.txt') | Out-Null
        Native @('checkin',$m.producer,'-c=Branch creation historical base') | Out-Null
        $base=Head
        Write-File 'parent.txt' "base two`n"
        Native @('checkin',$m.producer,'--all','-c=Branch creation newer parent') | Out-Null
        [IO.File]::WriteAllText($fixturePath,([ordered]@{base=$base;parentHead=(Head)} | ConvertTo-Json),$utf8)
    }
    if ($PrepareOnly) { Write-Host "Prepared $fixturePath"; return }
    $fixture=Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $suffix=[Guid]::NewGuid().ToString('N').Substring(0,8)
    $child=$m.branch+'/created 中文 & space-'+$suffix
    $sibling=$m.branch+'/partial-created-'+$suffix
    $selectorPath=Join-Path $m.producer '.plastic/plastic.selector'
    $selector=[IO.File]::ReadAllText($selectorPath)
    $partialSelector=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector'))
    $partialBytes=Snapshot $m.partial
    $partialConfiguration=PartialConfiguration
    Assert ([string]::IsNullOrWhiteSpace((Native @('status','--short','--machinereadable')))) 'Creation fixture starts clean'
    Invoke-BranchCli 'create-branch' @('--branch',$child,'--changeset',([string]$fixture.base),'--comment','Not authorized') 2 | Out-Null
    Invoke-BranchCli 'create-branch' @('--branch',($m.branch+"/invalid O'Brien-"+$suffix),'--changeset',([string]$fixture.base),'--comment','Invalid name','--yes') 2 | Out-Null
    $comment="Created via public CLI 中文`nsecond line & 'quote'"
    Write-File 'parent.txt' "uncommitted local bytes`n"
    Write-File 'private-local.txt' 'private bytes'
    try {
        $before=Snapshot
        Invoke-BranchCli 'create-branch' @('--branch',$child,'--changeset',([string]$fixture.base),'--comment',$comment,'--yes') | Out-Null
        Assert ((Snapshot) -ceq $before -and [IO.File]::ReadAllText($selectorPath) -ceq $selector) 'Creating server branch preserves dirty working tree and selector'
        $list=Invoke-BranchCli 'branches' @()
        $created=@($list.data.branches | Where-Object name -CEQ $child)
        Assert ($created.Count -eq 1 -and $created[0].parent -ceq $m.branch -and $created[0].headChangeset -eq $fixture.base) 'New branch has exact Unicode name, parent and historical base'
        Assert ($created[0].comment.Replace("`r`n","`n") -ceq $comment) 'Native branch retains multiline Unicode comment'
        Invoke-BranchCli 'create-branch' @('--branch',$child,'--changeset',([string]$fixture.base),'--comment','Duplicate','--yes') 2 | Out-Null
        Assert ((Snapshot) -ceq $before) 'Duplicate creation preserves local content'
    } finally {
        Native @('undo','parent.txt') | Out-Null
        [IO.File]::Delete((Join-Path $m.producer 'private-local.txt'))
    }
    Invoke-BranchCli 'create-branch' @('--branch',$sibling,'--changeset',([string]$fixture.base),'--comment','Created from Partial without loading','--yes') 0 $m.partial | Out-Null
    Assert ((Snapshot $m.partial) -ceq $partialBytes -and [IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector')) -ceq $partialSelector) 'Partial branch creation preserves loaded files and selector'
    Assert ((PartialConfiguration) -ceq $partialConfiguration) 'Partial branch creation preserves loading metadata and workspace mode'
    $empty=Invoke-BranchCli 'history-page' @('--branch',$child,'--limit','2','--before',([string]($fixture.parentHead+1)))
    Assert (@($empty.data.entries).Count -eq 0 -and $empty.data.hasMore) 'Empty new branch has no inherited commits and retains global cursor'
    Invoke-BranchCli 'switch-branch' @('--branch',$child,'--yes') | Out-Null
    Assert ([IO.File]::ReadAllText((Join-Path $m.producer 'parent.txt')) -ceq "base one`n") 'Created branch starts from requested historical snapshot'
    Write-File 'scope/one.txt' "first child commit`n"
    Native @('add','scope','-R') | Out-Null
    Native @('checkin',$m.producer,'-c=First own branch commit') | Out-Null
    $first=Head
    Write-File 'other.txt' "second child commit`n"
    Native @('add','other.txt') | Out-Null
    Native @('checkin',$m.producer,'-c=Second own branch commit') | Out-Null
    $second=Head
    Invoke-BranchCli 'switch-branch' @('--branch',$m.branch,'--yes') | Out-Null
    Write-File ('unrelated-'+$suffix+'.txt') 'parent-only commit'
    Native @('add',('unrelated-'+$suffix+'.txt')) | Out-Null
    Native @('checkin',$m.producer,'-c=Unrelated newer parent commit') | Out-Null
    $parentHead=Head
    $before=Snapshot
    $gap=Invoke-BranchCli 'history-page' @('--branch',$child,'--limit','1','--before',([string]($parentHead+1)))
    Assert (@($gap.data.entries).Count -eq 0 -and $gap.data.hasMore -and $gap.data.nextBeforeChangeset -eq $parentHead) 'Unrelated newest commit yields an empty resumable page'
    $page=Invoke-BranchCli 'history-page' @('--branch',$child,'--limit','2','--before',([string]$gap.data.nextBeforeChangeset))
    Assert (@($page.data.entries).Count -eq 2 -and $page.data.entries[0].changeset -eq $second -and $page.data.entries[1].changeset -eq $first) 'Branch cursor returns only own commits in descending order'
    Assert (@($page.data.entries | Where-Object branch -CNE $child).Count -eq 0) 'Branch filter compares exact Unicode names'
    $scope=Invoke-BranchCli 'history-page' @('--branch',$child,'--limit','3','--before',([string]($parentHead+1))) 0 (Join-Path $m.producer 'scope')
    Assert (@($scope.data.entries).Count -eq 1 -and $scope.data.entries[0].changeset -eq $first) 'Branch filter intersects absent directory scope'
    $partialHistory=Invoke-BranchCli 'history-page' @('--branch',$child,'--limit','3','--before',([string]($parentHead+1))) 0 $m.partial
    Assert (@($partialHistory.data.entries).Count -eq 2) 'Partial history reads branch without loading its tree'
    Assert ((Snapshot) -ceq $before -and (Snapshot $m.partial) -ceq $partialBytes) 'Branch history leaves both workspaces unchanged'
    Assert ((PartialConfiguration) -ceq $partialConfiguration) 'Partial history leaves loading metadata unchanged'
    Assert ([string]::IsNullOrWhiteSpace((Native @('status','--short','--machinereadable'))) -and [IO.File]::ReadAllText($selectorPath) -ceq $selector) 'Creation tests finish clean on original isolated branch'
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'branch-creation-last-run.json'),([ordered]@{child=$child;sibling=$sibling;base=$fixture.base;first=$first;second=$second;parentHead=$parentHead} | ConvertTo-Json),$utf8)
    Write-Host "PASS: $script:assertions branch creation/history integration assertions"
} finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'branch-creation-results.json'),($events.ToArray() | ConvertTo-Json -Depth 15),$utf8)
}
