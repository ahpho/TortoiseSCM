# GPL-2.0-or-later. Public CLI delete on dedicated native Standard/Partial workspaces.
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
function Run([string]$File,[string[]]$Arguments,[int]$Expected=0,[string]$Cwd=$m.producer) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=[IO.Path]::GetFullPath($File); $start.WorkingDirectory=$Cwd
    $start.Arguments=($Arguments | ForEach-Object { Quote $_ }) -join ' '
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $process=[Diagnostics.Process]::Start($start)
    try {
        $output=$process.StandardOutput.ReadToEndAsync(); $errors=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(90000)) { $process.Kill(); throw 'Branch delete subprocess timed out' }
        $stdout=$output.GetAwaiter().GetResult(); $stderr=$errors.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{file=$File;arguments=$Arguments;exitCode=$process.ExitCode;output=$stdout;error=$stderr})
        if ($process.ExitCode -ne $Expected) { throw "Expected $Expected got $($process.ExitCode): $stdout $stderr" }
        return $stdout
    } finally { $process.Dispose() }
}
function Invoke-BranchCli([string]$Command,[string[]]$Extra=@(),[int]$Expected=0,[string]$Root=$m.producer) {
    $response=Run $Executable (@('--cli','--json','--command',$Command,'--path',$Root,'--cm',$CmPath)+$Extra) $Expected | ConvertFrom-Json
    Assert ($response.success -eq ($Expected -eq 0) -and $response.exitCode -eq $Expected) "CLI $Command JSON agrees with exit"
    return $response
}
function Branch([string]$Name,[string]$Root=$m.producer) {
    $rows=@((Invoke-BranchCli 'branches' @() 0 $Root).data.branches | Where-Object name -ceq $Name)
    if ($rows.Count -ne 1) { throw "Expected one branch: $Name" }; return $rows[0]
}
function DeleteArgs($Row) {
    return @('--branch',$Row.name,'--branch-id',[string]$Row.branchId,'--branch-guid',$Row.guid,'--changeset',[string]$Row.headChangeset,'--yes')
}
function Snapshot([string]$Root) {
    $metadata=(Join-Path $Root '.plastic')+'\'
    return (@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Root.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
function State([string]$Root) {
    return (Snapshot $Root)+'|'+[IO.File]::ReadAllText((Join-Path $Root '.plastic/plastic.selector'))+'|'+
        (Run $CmPath @('status','--short','--machinereadable') 0 $Root)
}
function CreateBranch([string]$Name,[long]$Head=0) {
    if (!$Name.StartsWith($m.branch+'/',[StringComparison]::Ordinal)) { throw 'Only fixture descendants may be created' }
    Run $CmPath @('branch','create',"br:$Name@$($m.repository)","--changeset=cs:$Head@$($m.repository)",'-c=Isolated empty branch deletion test') | Out-Null
    return Branch $Name
}
function AssertMissing($Row) {
    $remaining=@((Invoke-BranchCli 'branches').data.branches | Where-Object { $_.name -ceq $Row.name -or $_.branchId -eq $Row.branchId -or $_.guid -eq $Row.guid })
    Assert ($remaining.Count -eq 0) 'Deleted name ID and GUID all absent on server'
}
$passed=$false; $failure=$null
try {
    $reference=State $m.referenceWorkspace
    $suffix=[Guid]::NewGuid().ToString('N').Substring(0,8)
    $empty=CreateBranch ($m.branch+'/empty 中文 & '+$suffix)
    Assert ($empty.headChangeset -eq 0 -and $empty.branchId -gt 0) 'Empty branch inherits cs:0 but has a native identity'
    $deleteArguments=DeleteArgs $empty
    foreach($option in @('--branch','--yes','--branch-id','--branch-guid','--changeset')) {
        $bad=New-Object 'Collections.Generic.List[string]'; $bad.AddRange([string[]]$deleteArguments)
        $i=$bad.IndexOf($option); $bad.RemoveAt($i); if($option -ne '--yes') { $bad.RemoveAt($i) }
        Invoke-BranchCli 'delete-branch' $bad.ToArray() 2 | Out-Null
    }
    foreach($pair in @(@('--branch-id','0'),@('--branch-id',([string]([long]$empty.branchId+1))),@('--branch-guid',[Guid]::Empty.ToString()),@('--branch-guid',[Guid]::NewGuid().ToString()),@('--changeset','1'))) {
        $bad=[string[]]$deleteArguments.Clone(); $bad[[Array]::IndexOf($bad,$pair[0])+1]=$pair[1]
        Invoke-BranchCli 'delete-branch' $bad 2 | Out-Null
    }
    Invoke-BranchCli 'delete-branch' ($deleteArguments+@('--new-name','not-accepted')) 2 | Out-Null
    $current=Branch $m.branch; Invoke-BranchCli 'delete-branch' (DeleteArgs $current) 2 | Out-Null
    $main=Branch '/main'; Invoke-BranchCli 'delete-branch' (DeleteArgs $main) 2 | Out-Null
    $child=CreateBranch ($empty.name+'/child')
    Invoke-BranchCli 'delete-branch' $deleteArguments 2 | Out-Null
    Invoke-BranchCli 'delete-branch' (DeleteArgs $child) | Out-Null
    AssertMissing $child

    # Branch attributes are server metadata; never silently discard them with a branch.
    $attribute='tortoisescm-autotest-delete-'+$suffix
    Run $CmPath @('attribute','create',$attribute) | Out-Null
    Run $CmPath @('attribute','set',"att:$attribute","br:$($empty.name)@$($m.repository)",'keep') | Out-Null
    Invoke-BranchCli 'delete-branch' $deleteArguments 2 | Out-Null
    Assert ((Branch $empty.name).guid -eq $empty.guid) 'Attribute-bearing branch remains unchanged'
    Run $CmPath @('attribute','unset',"att:$attribute","br:$($empty.name)@$($m.repository)") | Out-Null
    # Delete only the unused attribute type created by this test.
    Run $CmPath @('attribute','delete',"att:$attribute") | Out-Null

    # A real commit must make an otherwise eligible leaf undeletable.
    $owned=CreateBranch ($m.branch+'/has-history-'+$suffix)
    Run $CmPath @('switch',"br:$($owned.name)@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    $file=Join-Path $m.producer 'retained-history.txt'
    [IO.File]::WriteAllText($file,'published history must survive',$utf8)
    Run $CmPath @('add',$file) | Out-Null
    Run $CmPath @('checkin',$m.producer,'--all','-c=Branch deletion history guard') | Out-Null
    $owned=Branch $owned.name
    # A shelf based on the inherited head blocks even a different empty child conservatively.
    $shelfBlocked=CreateBranch ($m.branch+'/shelf-reference-'+$suffix) $owned.headChangeset
    [IO.File]::WriteAllText($file,'shelved and retained local change',$utf8)
    Run $CmPath @('shelve',$file,'--all',('-c=Branch deletion shelf guard '+$suffix)) | Out-Null
    [xml]$shelfXml=Run $CmPath @('find','shelve',"where parent = $($owned.headChangeset)",'--xml','--nototal','--encoding=utf-8')
    $shelves=@($shelfXml.PLASTICQUERY.SHELVE)
    Assert ($shelves.Count -eq 1) 'Isolated shelf references the inherited head changeset'
    $savedShelf=[string]$shelves[0].SHELVEID
    # Only undo the controlled file created by this fixture, after saving its shelf.
    Run $CmPath @('undo',$file) | Out-Null
    Run $CmPath @('switch',"br:$($m.branch)@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    Invoke-BranchCli 'delete-branch' (DeleteArgs $owned) 2 | Out-Null
    Invoke-BranchCli 'delete-branch' (DeleteArgs $shelfBlocked) 2 | Out-Null
    Assert ((Branch $owned.name).guid -eq $owned.guid) 'Branch with published changeset is retained'
    Assert ((Branch $shelfBlocked.name).guid -eq $shelfBlocked.guid) 'Empty branch sharing a shelf parent is conservatively retained'
    [xml]$stillShelved=Run $CmPath @('find','shelve',"where shelveid = $savedShelf",'--xml','--nototal','--encoding=utf-8')
    Assert (@($stillShelved.PLASTICQUERY.SHELVE).Count -eq 1) 'Deletion rejection preserves shelveset'
    # No shelf/history deletion: another empty branch inherits an independently committed head.
    $newBase=CreateBranch ($m.branch+'/new-base-'+$suffix)
    Run $CmPath @('switch',"br:$($newBase.name)@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    [IO.File]::WriteAllText($file,'independent retained parent history',$utf8)
    Run $CmPath @('add',$file) | Out-Null
    Run $CmPath @('checkin',$m.producer,'--all','-c=Empty child inherited head') | Out-Null
    $newBase=Branch $newBase.name
    Run $CmPath @('switch',"br:$($m.branch)@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    $partialEmpty=CreateBranch ($newBase.name+'/empty-child') $newBase.headChangeset
    foreach($root in @($m.producer,$m.partial)) { [IO.File]::WriteAllText((Join-Path $root 'private-uncommitted.txt'),'must survive metadata deletion',$utf8) }
    $standardState=State $m.producer; $partialState=State $m.partial
    Invoke-BranchCli 'delete-branch' $deleteArguments | Out-Null
    AssertMissing $empty
    Invoke-BranchCli 'delete-branch' (DeleteArgs $partialEmpty) 0 $m.partial | Out-Null
    AssertMissing $partialEmpty
    Assert ((Branch $newBase.name).headChangeset -eq $newBase.headChangeset) 'Deleting empty child preserves inherited parent history'
    Invoke-BranchCli 'delete-branch' $deleteArguments 2 | Out-Null
    $replacement=CreateBranch $empty.name
    Invoke-BranchCli 'delete-branch' $deleteArguments 2 | Out-Null
    Assert ((Branch $empty.name).guid -eq $replacement.guid -and $replacement.guid -ne $empty.guid) 'Old reviewed identity cannot delete a same-name replacement'
    Assert ((State $m.producer) -ceq $standardState -and (State $m.partial) -ceq $partialState) 'Deletion preserves dirty Standard and Partial files status and selectors'
    Assert ((State $m.referenceWorkspace) -ceq $reference) 'Original TestSCM is unchanged'
    $passed=$true
    Write-Host "PASS: $script:assertions branch delete integration assertions"
} catch { $failure=$_.Exception.ToString(); throw }
finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'branch-delete-results.json'),([ordered]@{success=$passed;assertions=$script:assertions;error=$failure;events=$events.ToArray()} | ConvertTo-Json -Depth 18),$utf8)
}
