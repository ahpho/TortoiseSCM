# GPL-2.0-or-later. Public CLI branch checks on a dedicated server branch.
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
$fixturePath = Join-Path $m.runDirectory 'branch-fixture.json'
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
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw 'Branch test subprocess timed out' }
        $output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{file=$File;arguments=$Arguments;exitCode=$process.ExitCode;output=$output;error=$errors})
        if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode): $output $errors" }
        return $output
    } finally { $process.Dispose() }
}
function Native([string[]]$Arguments) { Run $CmPath $Arguments }
function Invoke-BranchCli([string]$Command, [string[]]$Extra, [int]$Expected=0, [string]$Workspace=$m.producer) {
    $response = Run $Executable (@('--cli','--json','--command',$Command,'--path',$Workspace,'--cm',$CmPath)+$Extra) $Expected | ConvertFrom-Json
    Assert ($response.exitCode -eq $Expected -and $response.success -eq ($Expected -eq 0)) "CLI $Command process/JSON agree"
    return $response
}
function Head {
    $header = Native @('status','--header')
    if ($header -notmatch '\(cs:(\d+)') { throw "No changeset in status: $header" }
    return [long]$Matches[1]
}
function Write-File([string]$Relative, [string]$Content) {
    $path=Join-Path $m.producer $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path,$Content,$utf8)
}
function Snapshot {
    $metadata=(Join-Path $m.producer '.plastic')+'\'
    return (@(Get-ChildItem -LiteralPath $m.producer -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($m.producer.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
try {
    if (!$ValidateOnly) {
        if (Test-Path -LiteralPath $fixturePath) { throw 'Fixture already prepared; use -ValidateOnly' }
        if (@(Get-ChildItem -LiteralPath $m.producer -Force | Where-Object Name -ne '.plastic').Count) { throw 'Use a fresh empty fixture' }
        Write-File 'common.txt' "base`n"
        Write-File 'ignore.conf' "ignored-local.txt`n"
        Native @('add',$m.producer,'-R') | Out-Null
        Native @('checkin',$m.producer,'-c=Branch workflow baseline') | Out-Null
        $base=Head
        $child=$m.branch+'/feature 中文 & space'
        Native @('branch','create',('br:'+$child+'@'+$m.repository),('--changeset=cs:'+$base+'@'+$m.repository),'-c=Branch workflow feature') | Out-Null
        Native @('switch',('br:'+$child+'@'+$m.repository),('--workspace='+$m.producer)) | Out-Null
        Write-File 'feature.txt' "feature content`n"
        Native @('add','feature.txt') | Out-Null
        Native @('checkin',$m.producer,'-c=Branch workflow feature commit') | Out-Null
        $childHead=Head
        Native @('switch',('br:'+$m.branch+'@'+$m.repository),('--workspace='+$m.producer)) | Out-Null
        [IO.File]::WriteAllText($fixturePath,([ordered]@{base=$base;child=$child;childHead=$childHead} | ConvertTo-Json),$utf8)
    }
    if ($PrepareOnly) { Write-Host "Prepared $fixturePath"; return }
    $fixture=Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$fixture.child.StartsWith($m.branch+'/',[StringComparison]::Ordinal)) { throw 'Child branch escapes fixture branch' }
    $branch=[string]$fixture.child
    $selectorPath=Join-Path $m.producer '.plastic/plastic.selector'
    $selector=[IO.File]::ReadAllText($selectorPath)
    Assert ([string]::IsNullOrWhiteSpace((Native @('status','--short','--machinereadable')))) 'Branch fixture starts clean'
    $branches=Invoke-BranchCli 'branches' @()
    Assert (@($branches.data.branches | Where-Object { $_.name -eq $m.branch -and $_.isCurrent }).Count -eq 1) 'Branch list identifies current branch'
    Assert (@($branches.data.branches | Where-Object { $_.name -ceq $branch -and $_.headChangeset -eq $fixture.childHead }).Count -eq 1) 'Branch list preserves Unicode name and native head'
    $head=Invoke-BranchCli 'branch-head' @('--branch',$branch)
    Assert ($head.data.changeset -eq $fixture.childHead) 'Branch head resolves exact selected branch'
    $partial=Invoke-BranchCli 'branches' @() 0 $m.partial
    Assert (@($partial.data.branches | Where-Object name -ceq $branch).Count -eq 1) 'Partial workspace can browse repository branches'
    $partialSelector=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector'))
    $partialPrivate=Join-Path $m.partial ('switch-guard-'+[Guid]::NewGuid().ToString('N')+'.txt')
    [IO.File]::WriteAllText($partialPrivate,'Private content blocks Partial switch',$utf8)
    try {
        Invoke-BranchCli 'switch-branch' @('--branch',$branch,'--yes') 2 $m.partial | Out-Null
        Assert ([IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector')) -ceq $partialSelector -and
            [IO.File]::ReadAllText($partialPrivate) -ceq 'Private content blocks Partial switch') 'Dirty Partial switch rejection preserves selector and private bytes'
    } finally { Remove-Item -LiteralPath $partialPrivate }
    Invoke-BranchCli 'switch-branch' @('--branch',$branch) 2 | Out-Null
    Invoke-BranchCli 'switch-branch' @('--branch',$branch,'--yes') 2 (Join-Path $m.producer 'common.txt') | Out-Null
    Write-File 'common.txt' "local modified bytes`n"
    try {
        Invoke-BranchCli 'switch-branch' @('--branch',$branch,'--yes') 2 | Out-Null
        Assert ([IO.File]::ReadAllText((Join-Path $m.producer 'common.txt')) -ceq "local modified bytes`n" -and [IO.File]::ReadAllText($selectorPath) -ceq $selector) 'Pending edit blocks switch and preserves bytes/selector'
    } finally { Native @('undo','common.txt') | Out-Null }
    foreach ($name in @('private-local.txt','ignored-local.txt')) {
        $path=Join-Path $m.producer $name
        Write-File $name 'uncontrolled bytes'
        try {
            Invoke-BranchCli 'switch-branch' @('--branch',$branch,'--yes') 2 | Out-Null
            Assert ([IO.File]::ReadAllText($path) -ceq 'uncontrolled bytes' -and [IO.File]::ReadAllText($selectorPath) -ceq $selector) "Uncontrolled $name blocks switch without losing content"
        } finally { [IO.File]::Delete($path) }
    }
    Invoke-BranchCli 'switch-branch' @('--branch',$branch,'--yes') | Out-Null
    $after=Invoke-BranchCli 'branches' @()
    Assert (@($after.data.branches | Where-Object { $_.name -ceq $branch -and $_.isCurrent }).Count -eq 1) 'Public switch changes active branch selector'
    Assert ([IO.File]::ReadAllText((Join-Path $m.producer 'feature.txt')) -ceq "feature content`n") 'Public switch downloads selected branch content'
    Invoke-BranchCli 'switch-branch' @('--branch',$m.branch,'--yes') | Out-Null
    Assert (!(Test-Path -LiteralPath (Join-Path $m.producer 'feature.txt')) -and (Head) -eq $fixture.base) 'Switching back restores original branch tree'
    $before=Snapshot
    $merge=Invoke-BranchCli 'merge-preview' @('--changeset',([string]$fixture.childHead))
    Assert ($merge.data.sourceChangeset -eq $fixture.childHead -or $merge.data.plan.sourceChangeset -eq $fixture.childHead) 'Resolved branch head feeds existing merge preview'
    Assert ((Snapshot) -ceq $before -and [string]::IsNullOrWhiteSpace((Native @('status','--short','--machinereadable')))) 'Merge preview remains read-only'
    # A native merge in progress must also block switching. Undo affects only this clean fixture.
    try {
        Native @('merge',('cs:'+$fixture.childHead+'@'+$m.repository),'--merge','--nointeractiveresolution') | Out-Null
        $merged=Snapshot
        Invoke-BranchCli 'switch-branch' @('--branch',$branch,'--yes') 2 | Out-Null
        Assert ((Snapshot) -ceq $merged) 'Active native merge cannot be discarded by branch switch'
    } finally { Native @('undo',$m.producer,'-R') | Out-Null }
    Assert ([string]::IsNullOrWhiteSpace((Native @('status','--short','--machinereadable')))) 'Branch tests finish clean without publishing merge'
    Assert ([IO.File]::ReadAllText($selectorPath) -ceq $selector) 'Branch tests restore original isolated selector'
    Write-Host "PASS: $script:assertions branch integration assertions"
} finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'branch-results.json'),($events.ToArray() | ConvertTo-Json -Depth 15),$utf8)
}
