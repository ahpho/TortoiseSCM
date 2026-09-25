# GPL-2.0-or-later. Native feasibility of retaining an incoming-deleted directory.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ManifestPath,
    [string]$CmPath='D:\Program Files\PlasticSCM5\client\cm.exe', [switch]$FullWorkspace, [switch]$ExactAdds, [switch]$ReappearBeforeCheckin,
    [string]$Executable='')
$ErrorActionPreference='Stop'
$m=& (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $ManifestPath
$utf8=New-Object Text.UTF8Encoding($false)
$events=[Collections.Generic.List[object]]::new()
$result=Join-Path $m.runDirectory 'partial-directory-keep-deleted-probe.json'
function Save { [IO.File]::WriteAllText($result,($events.ToArray()|ConvertTo-Json -Depth 20),$utf8) }
function Assert([bool]$ok,[string]$message) { $events.Add([ordered]@{assertion=$message;passed=$ok}); Save; if(!$ok){throw $message}; Write-Host "PASS: $message" }
function Native([string]$cwd,[string[]]$arguments,[switch]$AllowFailure) {
    if(![IO.Path]::GetFullPath($cwd).StartsWith($m.runDirectory+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe native cwd'}
    Push-Location -LiteralPath $cwd
    try { $savedPreference=$ErrorActionPreference;$ErrorActionPreference='Continue';$text=@(& $CmPath @arguments 2>&1); $code=$LASTEXITCODE;$ErrorActionPreference=$savedPreference; $output=($text|ForEach-Object{"$_"}) -join "`n"
        $events.Add([ordered]@{cwd=$cwd;arguments=$arguments;exitCode=$code;output=$output}); Save
        if($code -ne 0 -and !$AllowFailure){throw "Native failed: $output"}; return $output
    } finally { Pop-Location }
}
function WriteFile([string]$workspace,[string]$relative,[string]$text){[IO.File]::WriteAllText((Join-Path $workspace $relative),$text,$utf8)}
function Rules { return [IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.fullycheckeddirectories')) }
function Full { return Test-Path -LiteralPath (Join-Path $m.partial '.plastic/plastic.fullupdate') }
function Tree([string]$workspace){[xml](Native $workspace @('ls',$workspace,'-R','--xml','--encoding=utf-8'))}
function OutsideState {
    $tree=Tree $m.partial
    $items=@($tree.LsResults.LsItems.LsItem|Where-Object{$_.WkPath -eq 'outside' -or $_.WkPath.StartsWith('outside\')}|ForEach-Object{"$($_.WkPath)|$($_.ItemId)|$($_.Changeset)"}|Sort-Object)
    return [ordered]@{items=($items -join "`n");loaded=[IO.File]::ReadAllText((Join-Path $m.partial 'outside/loaded.txt'));unloaded=Test-Path -LiteralPath (Join-Path $m.partial 'outside/unloaded.txt');sentinel=[IO.File]::ReadAllText((Join-Path $m.partial 'sentinel.txt'));selector=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector'))}|ConvertTo-Json -Compress
}
function Snapshot([string]$phase){$events.Add([ordered]@{phase=$phase;rules=Rules;full=Full;outside=OutsideState;status=Native $m.partial @('status','--short','--machinereadable')}); Native $m.partial @('ls',(Join-Path $m.partial 'old'),'-R','--xml','--encoding=utf-8') -AllowFailure|Out-Null;Save}
if((Test-Path -LiteralPath $result) -or @(Get-ChildItem -Force -LiteralPath $m.producer|Where-Object Name -ne '.plastic').Count){throw 'Use a fresh empty fixture'}
New-Item -ItemType Directory -Path (Join-Path $m.producer 'old/sub/empty'),(Join-Path $m.producer 'outside')|Out-Null
WriteFile $m.producer 'old/a.txt' 'base a';WriteFile $m.producer 'old/sub/b.txt' 'base b'
WriteFile $m.producer 'outside/loaded.txt' 'base outside';WriteFile $m.producer 'outside/unloaded.txt' 'unloaded outside';WriteFile $m.producer 'sentinel.txt' 'base sentinel'
Native $m.producer @('add',$m.producer,'-R')|Out-Null;Native $m.producer @('checkin',$m.producer,'--all','-c=Keep deleted directory native baseline')|Out-Null
Native $m.partial @('partial','update',$m.partial,'--dontmerge')|Out-Null
if(!$FullWorkspace){Native $m.partial @('partial','configure','-/outside/unloaded.txt')|Out-Null}
WriteFile $m.partial 'old/a.txt' 'local edit a';WriteFile $m.partial 'sentinel.txt' 'unrelated pending sentinel'
$oldTree=Tree $m.partial;$oldIds=@($oldTree.LsResults.LsItems.LsItem|Where-Object{$_.WkPath -eq 'old' -or $_.WkPath.StartsWith('old\')}|ForEach-Object{$_.ItemId})
$outside=OutsideState;$originalFull=Full;$originalRules=Rules
$backup=Join-Path $m.runDirectory 'keep-deleted-backup';Copy-Item -LiteralPath (Join-Path $m.partial 'old') -Destination $backup -Recurse
Native $m.producer @('remove',(Join-Path $m.producer 'old'))|Out-Null
WriteFile $m.producer 'outside/loaded.txt' 'incoming outside'
Native $m.producer @('checkin',$m.producer,'--all','-c=Delete selected directory with unrelated incoming bytes')|Out-Null
Snapshot 'before decision'
Native $m.partial @('partial','undo',(Join-Path $m.partial 'old/a.txt'))|Out-Null
Native $m.partial @('partial','configure','-/old')|Out-Null
Assert (!(Test-Path -LiteralPath (Join-Path $m.partial 'old'))) 'Exact configure unload removes incoming-deleted subtree'
Assert ((OutsideState) -ceq $outside) 'Directory unload preserves outside identities, revisions, bytes, exclusions, sentinel and selector'
$deletedRules=Rules;$deletedFull=Full;Snapshot 'after deletion'
Copy-Item -LiteralPath $backup -Destination (Join-Path $m.partial 'old') -Recurse
# Simulate interruption after only a prefix of the intended recursive addition.
Native $m.partial @('partial','add',(Join-Path $m.partial 'old'))|Out-Null
Native $m.partial @('partial','add',(Join-Path $m.partial 'old/a.txt'))|Out-Null
Snapshot 'interrupted after root and first file add'
Native $m.partial @('partial','undo',(Join-Path $m.partial 'old'),'-r','--added')|Out-Null
Assert ([IO.File]::ReadAllText((Join-Path $m.partial 'old/a.txt')) -ceq 'local edit a') 'Undo added subtree preserves local file bytes'
Assert (Test-Path -LiteralPath (Join-Path $m.partial 'old/sub/empty')) 'Undo added subtree preserves private empty directories'
Assert ((OutsideState) -ceq $outside) 'Exact recursive added-only undo preserves every outside item'
Assert ((Rules) -ceq $deletedRules -and (Full) -eq $deletedFull) 'Interrupted add undo preserves post-delete loading configuration'
Snapshot 'after interrupted add undo'
if($ExactAdds){
    $paths=@('old','old/sub','old/sub/empty','old/a.txt','old/sub/b.txt')
    foreach($path in $paths){Native $m.partial @('partial','add',(Join-Path $m.partial $path))|Out-Null}
    Snapshot 'complete exact additions before recovery'
    foreach($path in @('old/sub/b.txt','old/a.txt','old/sub/empty','old/sub','old')){Native $m.partial @('partial','undo',(Join-Path $m.partial $path),'--added')|Out-Null}
    Assert ([IO.File]::ReadAllText((Join-Path $m.partial 'old/a.txt')) -ceq 'local edit a' -and (Test-Path -LiteralPath (Join-Path $m.partial 'old/sub/empty'))) 'Bottom-up exact added-only undo preserves bytes and empty directories'
    Assert ((OutsideState) -ceq $outside -and (Rules) -ceq $deletedRules -and (Full) -eq $deletedFull) 'Bottom-up exact undo preserves outside state and configuration'
    foreach($path in $paths){Native $m.partial @('partial','add',(Join-Path $m.partial $path))|Out-Null}
}else{Native $m.partial @('partial','add',(Join-Path $m.partial 'old'),'-R')|Out-Null}
Snapshot 'after complete recursive add'
Assert ((OutsideState) -ceq $outside) 'Recursive re-add preserves outside scope'
Assert ((Rules) -ceq $deletedRules -and (Full) -eq $deletedFull) 'Re-add keeps post-delete loading configuration before checkin'
if($ReappearBeforeCheckin){
    New-Item -ItemType Directory -Path (Join-Path $m.producer 'old')|Out-Null
    WriteFile $m.producer 'old/server-new.txt' 'new server identity'
    Native $m.producer @('add',(Join-Path $m.producer 'old'),'-R')|Out-Null
    Native $m.producer @('checkin',(Join-Path $m.producer 'old'),'--all','-c=Reappear old directory with new server identity')|Out-Null
    if($Executable){
        foreach($selected in @('old','old/a.txt')){
            $start=New-Object Diagnostics.ProcessStartInfo
            $start.FileName=[IO.Path]::GetFullPath($Executable);$start.WorkingDirectory=$m.partial
            $arguments=@('--cli','--json','--command','checkin','--path',(Join-Path $m.partial $selected),'--cm',$CmPath,'--settings-file',(Join-Path $m.runDirectory 'keep-deleted-public-settings.xml'),'--yes','--comment','Reject server replacement of restored directory')
            $start.Arguments=($arguments|ForEach-Object{'"'+[regex]::Replace([regex]::Replace($_,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'})-join ' '
            $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.StandardOutputEncoding=$utf8
            $process=[Diagnostics.Process]::Start($start)
            try{
                $output=$process.StandardOutput.ReadToEndAsync();$errorOutput=$process.StandardError.ReadToEndAsync()
                if(!$process.WaitForExit(60000)){$process.Kill();throw 'Public checkin timed out'}
                $events.Add([ordered]@{executable=$Executable;arguments=$arguments;exitCode=$process.ExitCode;output=$output.Result;error=$errorOutput.Result});Save
                Assert ($process.ExitCode -eq 2) "Public checkin of $selected rejects a reappeared server directory"
            }finally{$process.Dispose()}
        }
    }else{Native $m.partial @('partial','checkin',(Join-Path $m.partial 'old'),'-c=Probe stale re-add against server replacement') -AllowFailure|Out-Null}
    Snapshot 'after attempted stale checkin'
    $events.Add([ordered]@{reappearanceProbeComplete=$true});Save;Write-Output $result;return
}
Native $m.partial @('partial','checkin',(Join-Path $m.partial 'old'),'-c=Explicitly retain deleted directory as new identities')|Out-Null
Snapshot 'after exact retained directory checkin'
$newTree=Tree $m.partial;$newItems=@($newTree.LsResults.LsItems.LsItem|Where-Object{$_.WkPath -eq 'old' -or $_.WkPath.StartsWith('old\')})
Assert ($newItems.Count -eq $oldIds.Count -and @($newItems|Where-Object{$oldIds -contains $_.ItemId}).Count -eq 0) 'Retained subtree receives all new item identities including empty directories'
Assert ((OutsideState) -ceq $outside) 'Exact directory checkin preserves outside scope and unrelated pending sentinel'
Native $m.consumer @('update',$m.consumer,'--dontmerge')|Out-Null
Assert ([IO.File]::ReadAllText((Join-Path $m.consumer 'old/a.txt')) -ceq 'local edit a' -and [IO.File]::ReadAllText((Join-Path $m.consumer 'old/sub/b.txt')) -ceq 'base b' -and (Test-Path -LiteralPath (Join-Path $m.consumer 'old/sub/empty'))) 'Independent consumer receives all original local bytes and empty directory'
Assert ([IO.File]::ReadAllText((Join-Path $m.consumer 'sentinel.txt')) -ceq 'base sentinel') 'Unrelated sentinel edit was not submitted'
$events.Add([ordered]@{complete=$true;fullWorkspace=[bool]$FullWorkspace;originalFull=$originalFull;originalRules=$originalRules;deletedFull=$deletedFull;deletedRules=$deletedRules;finalFull=Full;finalRules=Rules});Save
Write-Output $result
