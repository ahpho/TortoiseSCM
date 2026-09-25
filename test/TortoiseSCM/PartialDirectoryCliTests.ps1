# GPL-2.0-or-later. Real public executable directory workflow on an isolated fixture.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release/TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [switch]$FullWorkspace,
    [switch]$RepeatedMoveOnly
)
$ErrorActionPreference = 'Stop'
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $Manifest
$utf8 = New-Object Text.UTF8Encoding($false)
$events = New-Object 'Collections.Generic.List[object]'
$script:assertions = 0
$settings = Join-Path $m.runDirectory 'partial-directory-cli-settings.xml'
function Assert([bool]$Condition,[string]$Description) {
    $script:assertions++; $events.Add([pscustomobject]@{description=$Description;success=$Condition})
    if (!$Condition) { throw $Description }; Write-Host "PASS: $Description"
}
function Quote([string]$Value) { return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }
function Run([string]$File,[string]$Working,[string[]]$Arguments,[int]$Expected=0) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=[IO.Path]::GetFullPath($File); $start.WorkingDirectory=$Working
    $start.Arguments=($Arguments | ForEach-Object { Quote $_ }) -join ' '
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw 'Directory CLI subprocess timed out' }
        $output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{file=$File;cwd=$Working;arguments=$Arguments;exitCode=$process.ExitCode;output=$output;error=$errors})
        if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode): $output $errors" }
        return $output
    } finally { $process.Dispose() }
}
function Invoke-DirectoryCli([string]$Command,[string[]]$Extra=@(),[int]$Expected=0) {
    $json=Run $Executable $m.runDirectory (@('--cli','--json','--command',$Command,'--path',$m.partial,'--cm',$CmPath,'--settings-file',$settings)+$Extra) $Expected | ConvertFrom-Json
    if ($json.exitCode -ne $Expected -or $json.success -ne ($Expected -eq 0)) { throw 'JSON and process result differ' }; return $json
}
function Native([string]$Working,[string[]]$Arguments) {
    if (![IO.Path]::GetFullPath($Working).StartsWith($m.runDirectory+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test working directory' }
    return Run $CmPath $Working $Arguments
}
function Write-New([string]$Path,[string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $stream=New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
    try { $bytes=$utf8.GetBytes($Text); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
}
function Snapshot {
    $metadata=(Join-Path $m.partial '.plastic')+'\'
    return (@(Get-ChildItem -LiteralPath $m.partial -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($m.partial.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
$failure=$null
try {
    foreach ($role in @('producer','consumer','partial')) {
        if (@(Get-ChildItem -LiteralPath $m.$role -Force | Where-Object Name -ne '.plastic').Count) { throw 'Use a fresh empty fixture' }
    }
    Write-New (Join-Path $m.producer 'outside.txt') 'outside base'
    Write-New (Join-Path $m.producer 'unloaded/hidden.txt') 'hidden base'
    Native $m.producer @('add',$m.producer,'-R') | Out-Null
    Native $m.producer @('checkin',$m.producer,'-c=Directory CLI baseline') | Out-Null
    Native $m.partial @('partial','update',$m.partial,'--dontmerge') | Out-Null
    if (!$FullWorkspace) { Native $m.partial @('partial','configure','-/unloaded') | Out-Null }
    [IO.File]::WriteAllText((Join-Path $m.partial 'outside.txt'),'unrelated local edit',$utf8)
    $selector=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector'))
    $cases = if ($RepeatedMoveOnly) { @() } else { @('move-take','move-keep','delete-take','move-content') }
    foreach ($case in $cases) {
        Native $m.producer @('update',$m.producer,'--dontmerge') | Out-Null
        $old='/'+$case; $new='/'+$case+'-incoming'
        Write-New (Join-Path $m.producer ($case+'/a.txt')) ('base '+$case)
        Write-New (Join-Path $m.producer ($case+'/sub/b.txt')) ('clean '+$case)
        Native $m.producer @('add',(Join-Path $m.producer $case),'-R') | Out-Null
        Native $m.producer @('checkin',(Join-Path $m.producer $case),'-c=Directory CLI case base') | Out-Null
        Native $m.partial @('partial','configure',('+'+$old)) | Out-Null
        [IO.File]::WriteAllText((Join-Path $m.partial ($case+'/a.txt')),('local '+$case),$utf8)
        if ($case -eq 'delete-take') {
            Native $m.producer @('remove',(Join-Path $m.producer $case)) | Out-Null
        } else {
            Native $m.producer @('move',(Join-Path $m.producer $case),(Join-Path $m.producer ($case+'-incoming'))) | Out-Null
            if ($case -notin @('move-keep','move-content')) { [IO.File]::WriteAllText((Join-Path $m.producer ($case+'-incoming/a.txt')),('incoming '+$case),$utf8) }
            [IO.File]::WriteAllText((Join-Path $m.producer ($case+'-incoming/sub/b.txt')),('incoming clean '+$case),$utf8)
        }
        Native $m.producer @('checkin',$m.producer,'--all','-c=Directory CLI incoming change') | Out-Null
        $preview=Invoke-DirectoryCli 'partial-directory-preview'
        $conflict=@($preview.data.conflicts | Where-Object repositoryPath -eq $old)
        $choice=if ($case -eq 'move-keep') { 'keep-local' } else { 'take-incoming' }
        Assert ($conflict.Count -eq 1 -and $conflict[0].resolutionOptions -contains $choice) "Public directory preview supports $case"
        Assert (@($conflict[0].items | Where-Object { !$_.isDirectory }).Count -eq 2 -and @($conflict[0].items | Where-Object hasLocalChanges).Count -eq 1) 'Public preview enumerates all files and local edits'
        $before=Snapshot
        Invoke-DirectoryCli 'partial-directory-prepare' @('--item',$old) 2 | Out-Null
        $prepared=Invoke-DirectoryCli 'partial-directory-prepare' @('--item',$old,'--yes')
        $status=Invoke-DirectoryCli 'partial-directory-status'
        Assert ($status.data.ready -and $status.data.sessionId -eq $prepared.data.sessionId -and (Test-Path -LiteralPath $status.data.recoveryDirectory)) 'Separate CLI process resumes prepared directory backup'
        Assert ((Snapshot) -ceq $before) 'Directory prepare preserves working tree bytes'
        Invoke-DirectoryCli 'checkin' @('--comment','Must be blocked during directory decision','--yes') 2 | Out-Null
        Invoke-DirectoryCli 'partial-directory-resolve' @('--resolution',$choice) 2 | Out-Null
        if ($case -eq 'move-take') {
            Invoke-DirectoryCli 'partial-directory-cancel' @('--yes') | Out-Null
            Assert ($null -eq (Invoke-DirectoryCli 'partial-directory-status').data.sessionId -and (Snapshot) -ceq $before -and (Test-Path -LiteralPath $status.data.recoveryDirectory)) 'Cancel retires preparation while retaining tree and backups'
            $prepared=Invoke-DirectoryCli 'partial-directory-prepare' @('--item',$old,'--yes')
        }
        Invoke-DirectoryCli 'partial-directory-resolve' @('--resolution',$choice,'--yes') | Out-Null
        Assert ($null -eq (Invoke-DirectoryCli 'partial-directory-status').data.sessionId) "$case retires the verified directory session"
        Assert (!(Test-Path -LiteralPath (Join-Path $m.partial $case))) "$case leaves no obsolete directory"
        if ($case -ne 'delete-take') {
            $expected=if ($case -eq 'move-keep') { 'local '+$case } elseif ($case -eq 'move-content') { 'base '+$case } else { 'incoming '+$case }
            Assert ([IO.File]::ReadAllText((Join-Path $m.partial ($case+'-incoming/a.txt'))) -ceq $expected) "$case has selected content at new path"
            Assert ([IO.File]::ReadAllText((Join-Path $m.partial ($case+'-incoming/sub/b.txt'))) -ceq ('incoming clean '+$case)) "$case preserves incoming updates to locally clean files"
            if ($case -eq 'move-keep') {
                Run $Executable $m.runDirectory @('--cli','--json','--command','checkin','--path',(Join-Path $m.partial ($case+'-incoming')),'--cm',$CmPath,'--settings-file',$settings,'--comment','Publish reviewed directory local content','--yes') | Out-Null
            }
        }
        Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
        Assert (!(Test-Path -LiteralPath (Join-Path $m.consumer $case))) 'Independent consumer confirms original directory removed'
        if ($case -ne 'delete-take') { Assert ([IO.File]::ReadAllText((Join-Path $m.consumer ($case+'-incoming/a.txt'))) -ceq $expected) 'Independent consumer receives reviewed directory content' }
        if ($case -eq 'move-content') {
            $item='/move-content-incoming/a.txt'; $relative='move-content-incoming/a.txt'
            [IO.File]::WriteAllText((Join-Path $m.partial $relative),'local edit after directory move',$utf8)
            [IO.File]::WriteAllText((Join-Path $m.producer $relative),'incoming edit after directory move',$utf8)
            Native $m.producer @('checkin',(Join-Path $m.producer $relative),'--all','-c=Content conflict after pure directory move') | Out-Null
            $files=Invoke-DirectoryCli 'partial-conflict-prepare' @('--item',$item,'--yes')
            Assert ([IO.File]::ReadAllText($files.data.basePath) -ceq 'base move-content' -and [IO.File]::ReadAllText($files.data.localPath) -ceq 'local edit after directory move' -and [IO.File]::ReadAllText($files.data.remotePath) -ceq 'incoming edit after directory move') 'Content preparation follows historical identity across parent directory move'
            [IO.File]::WriteAllText($files.data.resultPath,'reviewed post-move content',$utf8)
            Invoke-DirectoryCli 'partial-conflict-resolve' @('--item',$item,'--result',$files.data.resultPath,'--yes') | Out-Null
            Assert ([IO.File]::ReadAllText((Join-Path $m.partial $relative)) -ceq 'reviewed post-move content') 'Post-move content resolution applies exact reviewed bytes'
            Run $Executable $m.runDirectory @('--cli','--json','--command','checkin','--path',(Join-Path $m.partial $relative),'--cm',$CmPath,'--settings-file',$settings,'--comment','Publish post-move reviewed content','--yes') | Out-Null
            Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
            Assert ([IO.File]::ReadAllText((Join-Path $m.consumer $relative)) -ceq 'reviewed post-move content') 'Independent consumer receives content resolved after parent move'
        }
        Assert ([IO.File]::ReadAllText((Join-Path $m.partial 'outside.txt')) -ceq 'unrelated local edit' -and (Test-Path -LiteralPath (Join-Path $m.partial 'unloaded/hidden.txt')) -eq [bool]$FullWorkspace -and [IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector')) -ceq $selector) 'Directory workflow preserves unrelated content, sibling loading and selector'
        Assert ((Test-Path -LiteralPath (Join-Path $m.partial '.plastic/plastic.fullupdate')) -eq [bool]$FullWorkspace) 'Directory workflow preserves full or explicit loading mode'
    }
    Native $m.producer @('update',$m.producer,'--dontmerge') | Out-Null
    Write-New (Join-Path $m.producer 'repeat-original/a.txt') 'repeat base'
    Write-New (Join-Path $m.producer 'repeat-original/sub/b.txt') 'repeat clean'
    Native $m.producer @('add',(Join-Path $m.producer 'repeat-original'),'-R') | Out-Null
    Native $m.producer @('checkin',(Join-Path $m.producer 'repeat-original'),'-c=Repeated pure directory move baseline') | Out-Null
    Native $m.partial @('partial','configure','+/repeat-original') | Out-Null
    $old='repeat-original'
    foreach ($new in @('repeat-first','repeat-second')) {
        Native $m.producer @('move',(Join-Path $m.producer $old),(Join-Path $m.producer $new)) | Out-Null
        Native $m.producer @('checkin',$m.producer,'--all','-c=Repeated pure directory move') | Out-Null
        [IO.File]::WriteAllText((Join-Path $m.partial ($old+'/a.txt')),('local '+$new),$utf8)
        $conflict=@((Invoke-DirectoryCli 'partial-directory-preview').data.conflicts | Where-Object repositoryPath -eq ('/'+$old))
        Assert ($conflict.Count -eq 1 -and $conflict[0].resolutionOptions -contains 'keep-local') 'Repeated pure directory move remains resolvable by historical identity'
        $prepared=Invoke-DirectoryCli 'partial-directory-prepare' @('--item',('/'+$old),'--yes')
        Assert ($prepared.data.ready -and (Test-Path -LiteralPath $prepared.data.recoveryDirectory)) 'Repeated move prepares durable backups through old historical paths'
        $choice=if ($new -eq 'repeat-first') { 'take-incoming' } else { 'keep-local' }
        Invoke-DirectoryCli 'partial-directory-resolve' @('--resolution',$choice,'--yes') | Out-Null
        $expected=if ($new -eq 'repeat-first') { 'repeat base' } else { 'local '+$new }
        Assert (!(Test-Path -LiteralPath (Join-Path $m.partial $old)) -and [IO.File]::ReadAllText((Join-Path $m.partial ($new+'/a.txt'))) -ceq $expected) 'Repeated move applies selected content at the exact new path'
        Assert ([IO.File]::ReadAllText((Join-Path $m.partial ($new+'/sub/b.txt'))) -ceq 'repeat clean') 'Repeated move preserves unchanged descendants'
        Assert ($null -eq (Invoke-DirectoryCli 'partial-directory-status').data.sessionId) 'Repeated move retires its verified session'
        Assert ((Test-Path -LiteralPath (Join-Path $m.partial '.plastic/plastic.fullupdate')) -eq [bool]$FullWorkspace) 'Repeated move preserves loading mode'
        $old=$new
    }
    Run $Executable $m.runDirectory @('--cli','--json','--command','checkin','--path',(Join-Path $m.partial 'repeat-second'),'--cm',$CmPath,'--settings-file',$settings,'--comment','Publish repeated-move local content','--yes') | Out-Null
    Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
    Assert ([IO.File]::ReadAllText((Join-Path $m.consumer 'repeat-second/a.txt')) -ceq 'local repeat-second' -and !(Test-Path -LiteralPath (Join-Path $m.consumer 'repeat-first'))) 'Independent consumer receives content submitted after two pure moves'
    Assert ([IO.File]::ReadAllText((Join-Path $m.partial 'outside.txt')) -ceq 'unrelated local edit' -and (Test-Path -LiteralPath (Join-Path $m.partial 'unloaded/hidden.txt')) -eq [bool]$FullWorkspace -and [IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector')) -ceq $selector) 'Repeated moves preserve unrelated bytes, loading and selector'
    Write-Host "PASS: $script:assertions real Partial directory CLI assertions"
} catch { $failure=$_.ToString(); throw }
finally { [IO.File]::WriteAllText((Join-Path $m.runDirectory 'partial-directory-cli-results.json'),([ordered]@{success=($null -eq $failure);assertions=$script:assertions;error=$failure;events=$events} | ConvertTo-Json -Depth 20),$utf8) }
