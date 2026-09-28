# GPL-2.0-or-later. Public CLI rename on dedicated native Standard/Partial workspaces.
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
        if (!$process.WaitForExit(90000)) { $process.Kill(); throw 'Branch rename subprocess timed out' }
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
function RenameArgs($Row,[string]$Leaf) {
    return @('--branch',$Row.name,'--branch-id',[string]$Row.branchId,'--branch-guid',$Row.guid,'--changeset',[string]$Row.headChangeset,'--new-name',$Leaf,'--yes')
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
$passed=$false; $failure=$null
try {
    $reference=State $m.referenceWorkspace
    $suffix=[Guid]::NewGuid().ToString('N').Substring(0,8)
    $old=$m.branch+'/rename-'+$suffix; $collision=$m.branch+'/taken-'+$suffix
    foreach($name in @($old,$collision)) {
        Run $CmPath @('branch','create',"br:$name@$($m.repository)","--changeset=cs:0@$($m.repository)",'-c=Isolated branch rename test') | Out-Null
    }
    # An actual branch-owned commit proves rename retains history, not merely an empty branch.
    Run $CmPath @('switch',"br:$old@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    [IO.File]::WriteAllText((Join-Path $m.producer 'rename-history.txt'),'history survives rename',$utf8)
    Run $CmPath @('add','rename-history.txt') | Out-Null
    Run $CmPath @('checkin',$m.producer,'-c=Branch rename history baseline') | Out-Null
    Run $CmPath @('switch',"br:$($m.branch)@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    $row=Branch $old
    Assert ($row.branchId -gt 0 -and [Guid]$row.guid -ne [Guid]::Empty -and $row.headChangeset -gt 0) 'Native immutable identity and own head exposed'
    $renameArguments=RenameArgs $row ('renamed 中文 & '+$suffix)
    foreach($option in @('--yes','--branch-id','--branch-guid','--new-name','--changeset')) {
        $bad=New-Object 'Collections.Generic.List[string]'; $bad.AddRange([string[]]$renameArguments)
        $i=$bad.IndexOf($option); $bad.RemoveAt($i); if($option -ne '--yes') { $bad.RemoveAt($i) }
        Invoke-BranchCli 'rename-branch' $bad.ToArray() 2 | Out-Null
    }
    foreach($pair in @(@('--branch-id','0'),@('--branch-id',([string]([long]$row.branchId+1))),@('--branch-guid',[Guid]::Empty.ToString()),@('--branch-guid',[Guid]::NewGuid().ToString()),@('--changeset','0'),@('--new-name','../bad'),@('--new-name','/full/path'),@('--new-name',('taken-'+$suffix)),@('--new-name','-option'))) {
        $bad=[string[]]$renameArguments.Clone(); $bad[[Array]::IndexOf($bad,$pair[0])+1]=$pair[1]
        Invoke-BranchCli 'rename-branch' $bad 2 | Out-Null
    }
    foreach($command in @('status','branches')) {
        foreach($pair in @(@('--branch-id',[string]$row.branchId),@('--branch-guid',$row.guid),@('--new-name','new'))) { Invoke-BranchCli $command $pair 2 | Out-Null }
    }
    $current=Branch $m.branch; Invoke-BranchCli 'rename-branch' (RenameArgs $current 'bad-current') 2 | Out-Null
    $main=Branch '/main'; Invoke-BranchCli 'rename-branch' (RenameArgs $main 'bad-root') 2 | Out-Null
    Run $CmPath @('branch','create',"br:$old/child@$($m.repository)","--changeset=cs:$($row.headChangeset)@$($m.repository)",'-c=Rename child reference guard') | Out-Null
    Invoke-BranchCli 'rename-branch' $renameArguments 2 | Out-Null
    # Delete only the empty child created above to permit testing the leaf operation.
    Run $CmPath @('branch','delete',"br:$old/child@$($m.repository)") | Out-Null
    Assert ((Branch $old).guid -eq $row.guid) 'All rejected rename attempts preserve original identity'
    foreach($root in @($m.producer,$m.partial)) { [IO.File]::WriteAllText((Join-Path $root 'private-uncommitted.txt'),'must survive metadata rename',$utf8) }
    $standardState=State $m.producer; $partialState=State $m.partial
    $result=Invoke-BranchCli 'rename-branch' $renameArguments
    $renamed=Branch $result.data.newBranch
    Assert ($renamed.branchId -eq $row.branchId -and $renamed.guid -eq $row.guid -and $renamed.headChangeset -eq $row.headChangeset -and $renamed.parent -ceq $row.parent) 'Standard rename preserves ID GUID head and parent'
    Assert (@((Invoke-BranchCli 'branches').data.branches | Where-Object name -ceq $old).Count -eq 0) 'Old branch name removed from server listing'
    $second=Invoke-BranchCli 'rename-branch' (RenameArgs $renamed ('partial-renamed-'+$suffix)) 0 $m.partial
    $final=Branch $second.data.newBranch $m.partial
    Assert ($final.branchId -eq $row.branchId -and $final.guid -eq $row.guid -and $final.headChangeset -eq $row.headChangeset) 'Partial rename preserves identity and committed history'
    Assert ((State $m.producer) -ceq $standardState -and (State $m.partial) -ceq $partialState) 'Metadata rename preserves dirty Standard and Partial files status and selectors'
    Assert ((State $m.referenceWorkspace) -ceq $reference) 'Original TestSCM is unchanged'
    $passed=$true
    Write-Host "PASS: $script:assertions branch rename integration assertions"
} catch { $failure=$_.Exception.ToString(); throw }
finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'branch-rename-results.json'),([ordered]@{success=$passed;assertions=$script:assertions;error=$failure;events=$events.ToArray()} | ConvertTo-Json -Depth 18),$utf8)
}
