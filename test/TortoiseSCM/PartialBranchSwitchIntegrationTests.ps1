# GPL-2.0-or-later. Public CLI Partial branch switching in isolated workspaces.
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
        if (!$process.WaitForExit(90000)) { $process.Kill(); throw 'Partial switch subprocess timed out' }
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
function WriteFixture([string]$Relative,[string]$Content) {
    $path=Join-Path $m.producer $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path,$Content,$utf8)
}
function LoadConfiguration {
    return (@('plastic.fullycheckeddirectories','plastic.fullupdate') | ForEach-Object {
        $path=Join-Path $m.partial ('.plastic/'+$_)
        if (Test-Path -LiteralPath $path) { $_+'|'+(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { $_+'|missing' }
    }) -join "`n"
}
function SwitchPartial([string]$Branch,[int]$Expected=0) {
    return Invoke-BranchCli 'switch-branch' @('--branch',$Branch,'--yes') $Expected $m.partial
}
function CurrentPartial([string]$Name) {
    $rows=@((Invoke-BranchCli 'branches' @() 0 $m.partial).data.branches | Where-Object { $_.isCurrent -and $_.name -ceq $Name })
    Assert ($rows.Count -eq 1) 'Partial current branch matches requested branch'
    $header=Run $CmPath @('status','--header','--xml','--encoding=utf-8') 0 $m.partial
    Assert ($header.Contains('<Changeset>-1</Changeset>')) 'Workspace remains native Partial mode'
}
$passed=$false; $failure=$null
try {
    $reference=State $m.referenceWorkspace
    foreach($file in @('loaded/base.txt','loaded/deleted.txt','loaded/excluded.txt','unloaded/base.txt','single/only.txt','single/other.txt')) { WriteFixture $file ('base '+$file) }
    WriteFixture 'ignore.conf' 'ignored-local.txt'
    Run $CmPath @('add',$m.producer,'-R') | Out-Null
    Run $CmPath @('checkin',$m.producer,'--all','-c=Partial branch switch base') | Out-Null
    $baseline=Branch $m.branch
    $target=$m.branch+'/switch 中文 & target'
    Run $CmPath @('branch','create',"br:$target@$($m.repository)","--changeset=cs:$($baseline.headChangeset)@$($m.repository)",'-c=Partial branch switch target') | Out-Null
    Run $CmPath @('switch',"br:$target@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    foreach($file in @('loaded/base.txt','loaded/excluded.txt','unloaded/base.txt','single/only.txt','single/other.txt')) { WriteFixture $file ('target '+$file) }
    WriteFixture 'loaded/new.txt' 'new in fully loaded directory'
    WriteFixture 'unloaded/new.txt' 'must remain unloaded'
    WriteFixture 'single/new.txt' 'must remain unloaded in individually selected directory'
    Run $CmPath @('add',(Join-Path $m.producer 'loaded/new.txt'),(Join-Path $m.producer 'unloaded/new.txt'),(Join-Path $m.producer 'single/new.txt')) | Out-Null
    Run $CmPath @('remove',(Join-Path $m.producer 'loaded/deleted.txt')) | Out-Null
    Run $CmPath @('checkin',$m.producer,'--all','-c=Partial branch target changes') | Out-Null
    $targetRow=Branch $target
    Run $CmPath @('switch',"br:$($m.branch)@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    $producerState=State $m.producer
    Run $CmPath @('partial','update','.','--report') 0 $m.partial | Out-Null
    Run $CmPath @('partial','configure','-/','+/loaded','+/ignore.conf') 0 $m.partial | Out-Null
    $rules=LoadConfiguration
    Assert ((Test-Path (Join-Path $m.partial 'loaded/base.txt')) -and !(Test-Path (Join-Path $m.partial 'unloaded'))) 'Fixture loads only requested directory and ignore rules'
    $cleanState=State $m.partial
    Invoke-BranchCli 'switch-branch' @('--branch',$target) 2 $m.partial | Out-Null
    Invoke-BranchCli 'switch-branch' @('--branch',$target,'--yes') 2 (Join-Path $m.partial 'loaded') | Out-Null
    SwitchPartial ($m.branch+'/not-present') 2 | Out-Null
    $loaded=Join-Path $m.partial 'loaded/base.txt'
    [IO.File]::WriteAllText($loaded,'uncommitted edit',$utf8)
    $dirty=State $m.partial
    SwitchPartial $target 2 | Out-Null
    Assert ((State $m.partial) -ceq $dirty) 'Modified Partial bytes and selector survive rejected switch'
    [IO.File]::WriteAllText($loaded,'base loaded/base.txt',$utf8)
    Run $CmPath @('partial','checkout',$loaded) 0 $m.partial | Out-Null
    $checkedOut=State $m.partial
    SwitchPartial $target 2 | Out-Null
    Assert ((State $m.partial) -ceq $checkedOut) 'Unmodified checkout blocks switch without clearing pending state'
    Run $CmPath @('partial','undo',$loaded) 0 $m.partial | Out-Null
    foreach($name in @('private-local.txt','ignored-local.txt')) {
        $private=Join-Path $m.partial $name
        [IO.File]::WriteAllText($private,'must not be discarded',$utf8)
        $dirty=State $m.partial
        SwitchPartial $target 2 | Out-Null
        Assert ((State $m.partial) -ceq $dirty) 'Private or ignored file blocks switch and keeps bytes'
        Remove-Item -LiteralPath $private
    }
    foreach($session in @('plastic.mergeprogress','tortoisescm-partial-directory.session','tortoisescm-structure.session','tortoisescm-partial.session')) {
        $marker=Join-Path $m.partial ('.plastic/'+$session)
        if (Test-Path -LiteralPath $marker) { throw 'Unexpected existing test marker' }
        [IO.File]::WriteAllText($marker,'test unfinished session',$utf8)
        try { SwitchPartial $target 2 | Out-Null } finally { Remove-Item -LiteralPath $marker }
    }
    Assert ((State $m.partial) -ceq $cleanState -and (LoadConfiguration) -ceq $rules) 'All rejected switches preserve clean baseline and loading configuration'
    $result=SwitchPartial $target
    Assert ($result.data.workspace.isPartial -and $result.data.branch -ceq $target) 'CLI returns resulting Partial workspace and requested branch'
    CurrentPartial $target
    Assert ((LoadConfiguration) -ceq $rules) 'Fully loaded directory rules preserved byte-for-byte'
    Assert ([IO.File]::ReadAllText($loaded) -ceq 'target loaded/base.txt' -and
        [IO.File]::ReadAllText((Join-Path $m.partial 'loaded/new.txt')) -ceq 'new in fully loaded directory' -and
        !(Test-Path (Join-Path $m.partial 'loaded/deleted.txt'))) 'Loaded changes additions and deletions follow target branch'
    Assert (!(Test-Path (Join-Path $m.partial 'unloaded')) -and !(Test-Path (Join-Path $m.partial 'single'))) 'Unloaded directories are not expanded'
    SwitchPartial $m.branch | Out-Null
    CurrentPartial $m.branch
    Assert ((LoadConfiguration) -ceq $rules -and [IO.File]::ReadAllText($loaded) -ceq 'base loaded/base.txt' -and
        (Test-Path (Join-Path $m.partial 'loaded/deleted.txt')) -and !(Test-Path (Join-Path $m.partial 'loaded/new.txt'))) 'Explicit switch back restores source content without changing loaded rules'
    # Native per-file selection and exclusions must remain sparse on the next branch.
    Run $CmPath @('partial','configure','-/','+/loaded','-/loaded/excluded.txt','+/single/only.txt','+/ignore.conf') 0 $m.partial | Out-Null
    $sparseRules=LoadConfiguration
    SwitchPartial $target | Out-Null
    Assert ((LoadConfiguration) -ceq $sparseRules) 'Sparse and individually selected file configuration retained'
    Assert (!(Test-Path (Join-Path $m.partial 'loaded/excluded.txt')) -and !(Test-Path (Join-Path $m.partial 'single/other.txt')) -and
        !(Test-Path (Join-Path $m.partial 'single/new.txt')) -and [IO.File]::ReadAllText((Join-Path $m.partial 'single/only.txt')) -ceq 'target single/only.txt') 'Excluded and unselected siblings remain unloaded; selected file updates'
    Assert ([string]::IsNullOrWhiteSpace((Run $CmPath @('status','--short','--machinereadable') 0 $m.partial))) 'Final Partial workspace has no pending changes'
    Assert ((State $m.producer) -ceq $producerState) 'Producer Standard workspace unchanged by Partial switch'
    Assert ((State $m.referenceWorkspace) -ceq $reference) 'Original TestSCM is unchanged'
    $passed=$true
    Write-Host "PASS: $script:assertions Partial branch switch integration assertions"
} catch { $failure=$_.Exception.ToString(); throw }
finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'partial-branch-switch-results.json'),([ordered]@{success=$passed;assertions=$script:assertions;error=$failure;events=$events.ToArray()} | ConvertTo-Json -Depth 18),$utf8)
}
