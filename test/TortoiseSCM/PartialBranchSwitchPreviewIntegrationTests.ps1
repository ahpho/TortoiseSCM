# GPL-2.0-or-later. Partial switch structure preview and before-write guard on isolated branches.
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
    foreach($file in @('loaded/keep.txt','loaded/nested/file.txt','unloaded/base.txt','sparse/only.txt','sparse/other.txt')) { WriteFixture $file ('base '+$file) }
    Run $CmPath @('add',$m.producer,'-R') | Out-Null
    Run $CmPath @('checkin',$m.producer,'--all','-c=Partial switch preview baseline') | Out-Null
    $baseline=Branch $m.branch
    $cases=@{}
    foreach($kind in @('moved','deleted','replaced','added','unloaded','content','sparse','sparse-moved')) {
        $name=$m.branch+'/preview-'+$kind
        Run $CmPath @('branch','create',"br:$name@$($m.repository)","--changeset=cs:$($baseline.headChangeset)@$($m.repository)",'-c=Partial switch structural fixture') | Out-Null
        Run $CmPath @('switch',"br:$name@$($m.repository)","--workspace=$($m.producer)") | Out-Null
        switch($kind) {
            'moved' { Run $CmPath @('move',(Join-Path $m.producer 'loaded'),(Join-Path $m.producer 'renamed 中文')) | Out-Null }
            'deleted' { Run $CmPath @('remove',(Join-Path $m.producer 'loaded/nested')) | Out-Null }
            'replaced' {
                Run $CmPath @('remove',(Join-Path $m.producer 'loaded/nested')) | Out-Null
                Run $CmPath @('checkin',$m.producer,'--all','-c=Remove original directory identity') | Out-Null
                WriteFixture 'loaded/nested/new-identity.txt' 'new directory identity'
                Run $CmPath @('add',(Join-Path $m.producer 'loaded/nested'),'-R') | Out-Null
            }
            'added' { WriteFixture 'loaded/newdir/added.txt' 'new selected subtree'; Run $CmPath @('add',(Join-Path $m.producer 'loaded/newdir'),'-R') | Out-Null }
            'unloaded' { WriteFixture 'unloaded/newdir/added.txt' 'must stay unloaded'; Run $CmPath @('add',(Join-Path $m.producer 'unloaded/newdir'),'-R') | Out-Null }
            'content' { WriteFixture 'loaded/keep.txt' 'target file content'; WriteFixture 'loaded/added-file.txt' 'new file allowed'; Run $CmPath @('add',(Join-Path $m.producer 'loaded/added-file.txt')) | Out-Null }
            'sparse' { WriteFixture 'sparse/newdir/added.txt' 'unselected sibling subtree'; Run $CmPath @('add',(Join-Path $m.producer 'sparse/newdir'),'-R') | Out-Null }
            'sparse-moved' {
                [IO.Directory]::CreateDirectory((Join-Path $m.producer 'sparse/new-parent')) | Out-Null
                Run $CmPath @('add',(Join-Path $m.producer 'sparse/new-parent')) | Out-Null
                Run $CmPath @('move',(Join-Path $m.producer 'sparse/only.txt'),(Join-Path $m.producer 'sparse/new-parent/only.txt')) | Out-Null
            }
        }
        Run $CmPath @('checkin',$m.producer,'--all',('-c=Preview case '+$kind)) | Out-Null
        $cases[$kind]=Branch $name
        Run $CmPath @('switch',"br:$($m.branch)@$($m.repository)","--workspace=$($m.producer)") | Out-Null
    }
    $producerState=State $m.producer
    Run $CmPath @('partial','update','.','--report') 0 $m.partial | Out-Null
    Run $CmPath @('partial','configure','-/','+/loaded') 0 $m.partial | Out-Null
    $before=State $m.partial; $config=LoadConfiguration
    foreach($kind in @('moved','deleted','replaced','added')) {
        $preview=Invoke-BranchCli 'partial-switch-preview' @('--branch',$cases[$kind].name) 0 $m.partial
        Assert (!$preview.data.canSwitch -and $preview.data.headChangeset -eq $cases[$kind].headChangeset) 'Preview blocks structural case at fixed target head'
        Assert (@($preview.data.directories | Where-Object { $_.change -ieq $kind }).Count -gt 0) ('Preview identifies native directory change: '+$kind)
        SwitchPartial $cases[$kind].name 2 | Out-Null
        Assert ((State $m.partial) -ceq $before -and (LoadConfiguration) -ceq $config) 'Preview and direct switch rejection preserve bytes selector and configuration'
    }
    foreach($kind in @('unloaded','content')) {
        $beforePreview=State $m.partial
        $preview=Invoke-BranchCli 'partial-switch-preview' @('--branch',$cases[$kind].name) 0 $m.partial
        Assert ($preview.data.canSwitch -and $preview.data.loadedDirectoryCount -ge 3) 'Unloaded-only directories or content changes permit switch'
        Assert ((State $m.partial) -ceq $beforePreview) 'Allowed preview is read-only'
        SwitchPartial $cases[$kind].name | Out-Null
        CurrentPartial $cases[$kind].name
        Assert ((LoadConfiguration) -ceq $config -and !(Test-Path (Join-Path $m.partial 'unloaded'))) 'Allowed switch preserves rules and unloaded directory'
        if($kind -eq 'content') { Assert ([IO.File]::ReadAllText((Join-Path $m.partial 'loaded/keep.txt')) -ceq 'target file content') 'Content updates remain supported after structural preflight' }
        SwitchPartial $m.branch | Out-Null
    }
    Run $CmPath @('partial','configure','-/','+/sparse/only.txt') 0 $m.partial | Out-Null
    $sparseBefore=State $m.partial; $sparseRules=LoadConfiguration
    $preview=Invoke-BranchCli 'partial-switch-preview' @('--branch',$cases['sparse-moved'].name) 0 $m.partial
    Assert (!$preview.data.canSwitch -and @($preview.data.directories | Where-Object { $_.change -eq 'Unsupported' }).Count -gt 0) 'Selected file moving into a new unloaded parent is blocked'
    SwitchPartial $cases['sparse-moved'].name 2 | Out-Null
    Assert ((State $m.partial) -ceq $sparseBefore -and (LoadConfiguration) -ceq $sparseRules) 'Sparse moved-file rejection preserves bytes selector and rules'
    $preview=Invoke-BranchCli 'partial-switch-preview' @('--branch',$cases['sparse'].name) 0 $m.partial
    Assert ($preview.data.canSwitch -and $preview.data.loadingRuleCount -eq 0) 'Unselected new sibling directory does not expand sparse parent'
    Assert ((State $m.partial) -ceq $sparseBefore) 'Sparse preview preserves current tree'
    SwitchPartial $cases['sparse'].name | Out-Null
    Assert (!(Test-Path (Join-Path $m.partial 'sparse/newdir')) -and !(Test-Path (Join-Path $m.partial 'sparse/other.txt')) -and (LoadConfiguration) -ceq $sparseRules) 'Sparse selection remains sparse after allowed switch'
    # Full loaded scope must also catch an otherwise unloaded target directory addition.
    SwitchPartial $m.branch | Out-Null
    Run $CmPath @('partial','configure','-/','+/') 0 $m.partial | Out-Null
    $fullBefore=State $m.partial; $fullRules=LoadConfiguration
    $preview=Invoke-BranchCli 'partial-switch-preview' @('--branch',$cases['unloaded'].name) 0 $m.partial
    Assert (!$preview.data.canSwitch -and $preview.data.isFullyLoaded) 'Full loading treats target new directories as blockers'
    SwitchPartial $cases['unloaded'].name 2 | Out-Null
    Assert ((State $m.partial) -ceq $fullBefore -and (LoadConfiguration) -ceq $fullRules) 'Full loading structure guard blocks before mutation'
    Invoke-BranchCli 'partial-switch-preview' @('--branch',$cases['content'].name,'--yes') 2 $m.partial | Out-Null
    Invoke-BranchCli 'partial-switch-preview' @('--branch',$cases['content'].name) 2 $m.producer | Out-Null
    Assert ((State $m.producer) -ceq $producerState) 'Standard producer preserved'
    Assert ((State $m.referenceWorkspace) -ceq $reference) 'Original TestSCM is unchanged'
    $passed=$true; Write-Host "PASS: $script:assertions Partial switch preview integration assertions"
} catch { $failure=$_.Exception.ToString(); throw }
finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'partial-switch-preview-results.json'),([ordered]@{success=$passed;assertions=$script:assertions;error=$failure;events=$events.ToArray()} | ConvertTo-Json -Depth 18),$utf8)
}
