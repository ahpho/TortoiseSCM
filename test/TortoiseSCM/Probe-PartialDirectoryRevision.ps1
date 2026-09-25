# GPL-2.0-or-later. Confirm that a fully loaded directory may mix hierarchy revisions.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ManifestPath,
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe')
$ErrorActionPreference = 'Stop'
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $ManifestPath
$utf8 = New-Object Text.UTF8Encoding($false)
$events = [Collections.Generic.List[object]]::new()
$result = Join-Path $m.runDirectory 'partial-directory-revision-probe.json'
function Native([string]$cwd,[string[]]$arguments) {
    if (![IO.Path]::GetFullPath($cwd).StartsWith($m.runDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe native cwd' }
    Push-Location -LiteralPath $cwd
    try {
        $saved=$ErrorActionPreference; $ErrorActionPreference='Continue'
        $lines=@(& $CmPath @arguments 2>&1); $code=$LASTEXITCODE; $ErrorActionPreference=$saved
        $output=($lines | ForEach-Object {"$_"}) -join "`n"
        $events.Add([ordered]@{cwd=$cwd;arguments=$arguments;exitCode=$code;output=$output})
        [IO.File]::WriteAllText($result,($events.ToArray()|ConvertTo-Json -Depth 10),$utf8)
        if($code -ne 0){throw "Native revision probe failed: $output"}; return $output
    } finally {Pop-Location}
}
function WriteFile([string]$workspace,[string]$path,[string]$content){[IO.File]::WriteAllText((Join-Path $workspace $path),$content,$utf8)}
if ((Test-Path -LiteralPath $result) -or (Get-ChildItem -LiteralPath $m.producer -Force | Where-Object {$_.Name -ne '.plastic'}).Count) {throw 'Use an empty fresh fixture'}
New-Item -ItemType Directory -Path (Join-Path $m.producer 'old/sub'),(Join-Path $m.producer 'outside') | Out-Null
WriteFile $m.producer 'old/sub/base.txt' 'base child'
WriteFile $m.producer 'outside/loaded.txt' 'outside loaded'
WriteFile $m.producer 'outside/unloaded.txt' 'outside unloaded'
Native $m.producer @('add',$m.producer,'-R') | Out-Null
Native $m.producer @('checkin',$m.producer,'--all','-c=Directory revision baseline') | Out-Null
WriteFile $m.producer 'old/sub/later.txt' 'later child'
Native $m.producer @('add',(Join-Path $m.producer 'old/sub/later.txt')) | Out-Null
Native $m.producer @('checkin',(Join-Path $m.producer 'old'),'--all','-c=Directory revision later descendant') | Out-Null
Native $m.partial @('partial','configure','-/') | Out-Null
Native $m.partial @('partial','configure','+/old','+/outside/loaded.txt') | Out-Null
Native $m.partial @('ls',(Join-Path $m.partial 'old'),'-R','--xml','--encoding=utf-8') | Out-Null
Native $m.partial @('fileinfo',(Join-Path $m.partial 'old'),(Join-Path $m.partial 'old/sub'),'--fields=RevisionChangeset,Type,Status','--xml','--encoding=utf-8') | Out-Null
Native $m.producer @('move',(Join-Path $m.producer 'old'),(Join-Path $m.producer 'new')) | Out-Null
Native $m.producer @('checkin',$m.producer,'--all','-c=Directory revision incoming move') | Out-Null
WriteFile $m.partial 'old/sub/base.txt' 'local child edit'
Native $m.partial @('status','--short','--machinereadable') | Out-Null
Write-Output $result
