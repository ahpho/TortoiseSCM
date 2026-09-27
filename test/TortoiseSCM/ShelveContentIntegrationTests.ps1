# GPL-2.0-or-later. Run after ShelvesIntegrationTests on an isolated fixture.
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release/TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe'
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
function Invoke-ShelveCli([string]$command, [string]$workspace, [string[]]$extra=@(), [int]$expected=0) {
    Run $Executable $workspace (@('--cli','--json','--cm',$CmPath,'--command',$command,'--path',$workspace)+$extra) $expected | ConvertFrom-Json
}
$shelveId=$null; $applied=$false
$output=Join-Path $m.runDirectory ('shelve-content-'+[Guid]::NewGuid().ToString('N'))
$selector=[IO.File]::ReadAllText((Join-Path $m.consumer '.plastic/plastic.selector'))
try {
    Assert ([string]::IsNullOrWhiteSpace((Run $CmPath $m.consumer @('status','--short','--machinereadable')))) 'Consumer starts clean'
    $paths=@('selected 中文 & folder/one.txt','selected 中文 & folder/two.txt')
    $comment='TortoiseSCM content validation '+[Guid]::NewGuid().ToString('N')
    # Explicit pending file selection creates a new disposable shelveset; saved fixtures are retained.
    $args=@('--cli','--json','--cm',$CmPath,'--command','shelve-create','--comment',$comment,'--yes')
    foreach ($path in $paths) { $args+=@('--path',(Join-Path $m.producer $path)) }
    Run $Executable $m.producer $args | Out-Null
    $saved=@((Invoke-ShelveCli 'shelves' $m.producer).data.shelves | Where-Object comment -ceq $comment)
    Assert ($saved.Count -eq 1) 'Disposable shelveset identified by unique comment'
    $shelveId=[string]$saved[0].shelveId
    foreach ($workspace in @($m.producer,$m.partial)) {
        $diff=Invoke-ShelveCli 'shelve-diff' $workspace @('--shelve',$shelveId)
        Assert (@($diff.data.files).Count -eq 2 -and @($diff.data.files | Where-Object { !$_.diff.hasChanges }).Count -eq 0) 'Standard and Partial read complete shelveset diffs'
        Assert ($diff.output.Contains('-base') -and $diff.output.Contains('+pending producer')) 'Diff compares actual parent bytes to shelved bytes'
    }
    Invoke-ShelveCli 'shelve-export' $m.producer @('--shelve',$shelveId,'--output',$output,'--yes') | Out-Null
    foreach ($path in $paths) { Assert ((Get-FileHash -LiteralPath (Join-Path $output $path)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $m.producer $path)).Hash) "Export byte hash matches $path" }
    Assert (!(Test-Path -LiteralPath (Join-Path $output 'outside.txt'))) 'Export excludes unselected changes'
    $first=Join-Path $output $paths[0]; [IO.File]::WriteAllText($first,'preserve existing',$utf8)
    Invoke-ShelveCli 'shelve-export' $m.producer @('--shelve',$shelveId,'--output',$output,'--yes') 2 | Out-Null
    Assert ([IO.File]::ReadAllText($first) -ceq 'preserve existing') 'Unconfirmed overwrite preserves output'
    Invoke-ShelveCli 'shelve-export' $m.producer @('--shelve',$shelveId,'--output',$output,'--yes','--overwrite') | Out-Null
    Assert ((Get-FileHash -LiteralPath $first).Hash -eq (Get-FileHash -LiteralPath (Join-Path $m.producer $paths[0])).Hash) 'Explicit overwrite restores shelved bytes'
    Invoke-ShelveCli 'shelve-apply' $m.partial @('--shelve',$shelveId,'--yes') 2 | Out-Null
    Invoke-ShelveCli 'shelve-apply' $m.producer @('--shelve',$shelveId,'--yes') 2 | Out-Null
    $applied=$true
    Invoke-ShelveCli 'shelve-apply' $m.consumer @('--shelve',$shelveId,'--yes') | Out-Null
    foreach ($path in $paths) { Assert ((Get-FileHash -LiteralPath (Join-Path $m.consumer $path)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $m.producer $path)).Hash) "CLI apply restores exact bytes for $path" }
    Assert ([IO.File]::ReadAllText((Join-Path $m.consumer '.plastic/plastic.selector')) -ceq $selector) 'Apply preserves consumer selector'
    Invoke-ShelveCli 'shelve-delete' $m.consumer @('--shelve',$shelveId,'--yes') | Out-Null
    Assert (@((Invoke-ShelveCli 'shelves' $m.consumer).data.shelves | Where-Object shelveId -eq $shelveId).Count -eq 0) 'CLI delete removes only the disposable shelveset'
    $shelveId=$null
} finally {
    try {
        if ($applied) {
            Run $CmPath $m.consumer @('undo',$m.consumer,'-R') | Out-Null
            Assert ([string]::IsNullOrWhiteSpace((Run $CmPath $m.consumer @('status','--short','--machinereadable')))) 'Consumer finishes clean'
        }
    } finally {
        [IO.File]::WriteAllText((Join-Path $m.runDirectory 'shelve-content-results.json'),($events.ToArray() | ConvertTo-Json -Depth 20),$utf8)
    }
    if ($null -ne $shelveId) { Write-Warning "Inspect disposable shelveset sh:$shelveId; failure does not trigger a blind delete retry." }
}
Write-Host "PASS: $assertions shelveset content integration assertions"
