# GPL-2.0-or-later. Public CLI comparison checks on a dedicated server branch.
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
$fixturePath = Join-Path $m.runDirectory 'changeset-comparison-fixture.json'
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
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw 'Comparison test subprocess timed out' }
        $output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{file=$File;arguments=$Arguments;exitCode=$process.ExitCode;output=$output;error=$errors})
        if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode): $output $errors" }
        return $output
    } finally { $process.Dispose() }
}
function Native([string[]]$Arguments) { Run $CmPath $Arguments }
function Invoke-ComparisonCli([string]$Command, [string[]]$Extra, [int]$Expected=0, [string]$Workspace=$m.producer) {
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
        Write-File 'changed.txt' "before`n"
        Write-File 'gone.txt' "deleted content`n"
        Write-File 'old 中文 & name.txt' "moved before`n"
        Write-File 'stable-move.txt' "unchanged moved content`n"
        Write-File 'reverted.txt' "original`n"
        Write-File 'old-dir/child.txt' "directory child`n"
        [IO.File]::WriteAllBytes((Join-Path $m.producer 'binary.bin'),[byte[]]@(0,255,1,0))
        Native @('add',$m.producer,'-R') | Out-Null
        Native @('checkin',$m.producer,'-c=Changeset comparison baseline') | Out-Null
        $from=Head
        Native @('move','old 中文 & name.txt','new 中文 & name.txt') | Out-Null
        Native @('move','stable-move.txt','stable-moved.txt') | Out-Null
        Native @('move','old-dir','new-dir') | Out-Null
        Native @('remove','gone.txt') | Out-Null
        Write-File 'changed.txt' "after`n"
        Write-File 'new 中文 & name.txt' "moved after`n"
        Write-File 'new-dir/child.txt' "directory child changed`n"
        Write-File 'reverted.txt' "temporary`n"
        Write-File 'added.txt' "added content`n"
        Write-File 'transient.txt' "temporary item`n"
        [IO.Directory]::CreateDirectory((Join-Path $m.producer 'empty-added')) | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $m.producer 'binary.bin'),[byte[]]@(0,255,2,0))
        Native @('add','added.txt','transient.txt','empty-added') | Out-Null
        Native @('checkin',$m.producer,'--all','-c=Changeset comparison mixed changes') | Out-Null
        $intermediate=Head
        Native @('remove','transient.txt') | Out-Null
        Write-File 'reverted.txt' "original`n"
        Native @('checkin',$m.producer,'--all','-c=Changeset comparison net result') | Out-Null
        $to=Head
        [IO.File]::WriteAllText($fixturePath,([ordered]@{from=$from;intermediate=$intermediate;to=$to} | ConvertTo-Json),$utf8)
    }
    if ($PrepareOnly) { Write-Host "Prepared $fixturePath"; return }
    $fixture=Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $from=[string]$fixture.from; $to=[string]$fixture.to
    $selector=[IO.File]::ReadAllText((Join-Path $m.producer '.plastic/plastic.selector'))
    $before=Snapshot
    Assert ([string]::IsNullOrWhiteSpace((Native @('status','--short','--machinereadable')))) 'Fixture starts clean'
    $comparison=Invoke-ComparisonCli 'diff-changesets' @('--from',$from,'--to',$to)
    $files=@($comparison.data.files)
    Assert (@($files | Where-Object { $_.status -eq 'A' -and $_.path -eq '/added.txt' }).Count -eq 1) 'Forward comparison includes added file'
    Assert (@($files | Where-Object { $_.status -eq 'D' -and $_.path -eq '/gone.txt' }).Count -eq 1) 'Forward comparison includes deleted file'
    Assert (@($files | Where-Object { $_.status -eq 'C' -and $_.path -eq '/changed.txt' }).Count -eq 1) 'Forward comparison includes content change'
    Assert (@($files | Where-Object { $_.status -eq 'M' -and $_.path -eq '/new 中文 & name.txt' -and $_.oldPath -eq '/old 中文 & name.txt' }).Count -eq 1) 'Move retains exact old/new Unicode paths'
    Assert (@($files | Where-Object { $_.status -eq 'M' -and $_.path -eq '/new-dir' -and $_.oldPath -eq '/old-dir' -and $_.itemType -eq 'D' }).Count -eq 1) 'Directory move is a structural difference'
    Assert (@($files | Where-Object { $_.status -eq 'C' -and $_.path -eq '/new-dir/child.txt' -and $_.oldPath -eq '/old-dir/child.txt' }).Count -eq 1) 'Changed descendant follows its historical directory move'
    Assert (@($files | Where-Object { $_.status -eq 'A' -and $_.path -eq '/empty-added' -and $_.itemType -eq 'D' }).Count -eq 1) 'Added empty directory remains visible'
    Assert (@($files | Where-Object path -eq '/transient.txt').Count -eq 0) 'Net comparison excludes item added and deleted between endpoints'
    $reverse=Invoke-ComparisonCli 'diff-changesets' @('--from',$to,'--to',$from)
    Assert (@($reverse.data.files | Where-Object { $_.status -eq 'D' -and $_.path -eq '/added.txt' }).Count -eq 1 -and @($reverse.data.files | Where-Object { $_.status -eq 'A' -and $_.path -eq '/gone.txt' }).Count -eq 1) 'Reverse comparison swaps additions and deletions'
    Assert (@($reverse.data.files | Where-Object { $_.status -eq 'M' -and $_.path -eq '/old 中文 & name.txt' -and $_.oldPath -eq '/new 中文 & name.txt' }).Count -eq 1) 'Reverse comparison swaps move endpoints'
    $same=Invoke-ComparisonCli 'diff-changesets' @('--from',$to,'--to',$to)
    Assert (@($same.data.files).Count -eq 0) 'Identical changesets produce an empty list'
    $partial=Invoke-ComparisonCli 'diff-changesets' @('--from',$from,'--to',$to) 0 $m.partial
    Assert ((ConvertTo-Json @($partial.data.files) -Compress) -eq (ConvertTo-Json $files -Compress)) 'Partial workspace reads complete server comparison without loading files'
    $moved=Invoke-ComparisonCli 'diff-history' @('--from',$from,'--to',$to,'--from-item','/old 中文 & name.txt','--item','/new 中文 & name.txt')
    Assert ($moved.data.hasChanges -and $moved.data.diffText.Contains('-moved before') -and $moved.data.diffText.Contains('+moved after')) 'Moved file comparison reads both historical paths'
    $stable=Invoke-ComparisonCli 'diff-history' @('--from',$from,'--to',$to,'--from-item','/stable-move.txt','--item','/stable-moved.txt')
    Assert (!$stable.data.hasChanges) 'Pure rename compares equal file bytes'
    $child=Invoke-ComparisonCli 'diff-history' @('--from',$from,'--to',$to,'--from-item','/old-dir/child.txt','--item','/new-dir/child.txt')
    Assert ($child.data.hasChanges -and $child.data.diffText.Contains('+directory child changed')) 'Changed descendant compares content across directory move'
    $binary=Invoke-ComparisonCli 'diff-history' @('--from',$from,'--to',$to,'--item','/binary.bin')
    Assert ($binary.data.isBinary -and $binary.data.hasChanges) 'Binary comparison reports changed bytes'
    $reverted=Invoke-ComparisonCli 'diff-history' @('--from',$from,'--to',$to,'--item','/reverted.txt')
    Assert (!$reverted.data.hasChanges) 'Content restored between endpoints compares equal'
    $deletedOutput=Join-Path $m.runDirectory 'deleted-export.txt'
    Invoke-ComparisonCli 'export' @('--changeset',$from,'--item','/gone.txt','--output',$deletedOutput,'--yes','--overwrite') | Out-Null
    Assert ([IO.File]::ReadAllText($deletedOutput) -eq "deleted content`n") 'Deleted file can export its existing source endpoint'
    Assert ((Snapshot) -ceq $before -and [IO.File]::ReadAllText((Join-Path $m.producer '.plastic/plastic.selector')) -ceq $selector) 'Comparisons preserve working files and selector'
    Assert ([string]::IsNullOrWhiteSpace((Native @('status','--short','--machinereadable')))) 'Comparisons leave fixture clean'
    Write-Host "PASS: $script:assertions changeset comparison integration assertions"
} finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'changeset-comparison-results.json'),($events.ToArray() | ConvertTo-Json -Depth 15),$utf8)
}
