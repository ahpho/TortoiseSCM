# GPL-2.0-or-later. Research native directory operations in isolated fixtures only.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ManifestPath,
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [ValidateSet('Move','Delete')][string]$Mode = 'Move',
    [switch]$Selective, [switch]$Private, [switch]$UnrelatedDelete, [switch]$FullWorkspace,
    [ValidateSet('Directory','ExactFiles')][string]$Load = 'Directory')
$ErrorActionPreference = 'Stop'
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $ManifestPath
$run = $m.runDirectory
$utf8 = New-Object Text.UTF8Encoding($false)
$events = [Collections.Generic.List[object]]::new()
$result = Join-Path $run 'partial-directory-native-probe.json'
function Save { [IO.File]::WriteAllText($result, ($events.ToArray() | ConvertTo-Json -Depth 20), $utf8) }
function Native([string]$cwd,[string[]]$arguments,[switch]$AllowFailure) {
    if (![IO.Path]::GetFullPath($cwd).StartsWith($run + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe native cwd' }
    Push-Location -LiteralPath $cwd
    try {
        $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $lines = @(& $CmPath @arguments 2>&1); $code = $LASTEXITCODE; $ErrorActionPreference = $saved
        $output = ($lines | ForEach-Object { "$_" }) -join "`n"
        $events.Add([ordered]@{cwd=$cwd; arguments=$arguments; exitCode=$code; output=$output}); Save
        if ($code -ne 0 -and !$AllowFailure) { throw "cm failed: $($arguments -join ' '): $output" }
        return $output
    } finally { Pop-Location }
}
function WriteFile([string]$relative,[string]$text,[string]$workspace=$m.producer) { [IO.File]::WriteAllText((Join-Path $workspace $relative), $text, $utf8) }
function Snapshot([string]$label) {
    $files = @(Get-ChildItem -LiteralPath $m.partial -File -Recurse -Force | Where-Object { !$_.FullName.StartsWith((Join-Path $m.partial '.plastic') + '\') } | ForEach-Object { [ordered]@{path=$_.FullName.Substring($m.partial.Length); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; text=[IO.File]::ReadAllText($_.FullName)} })
    $events.Add([ordered]@{label=$label; files=$files; selector=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.selector')); rules=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic/plastic.fullycheckeddirectories')); fullUpdate=(Test-Path -LiteralPath (Join-Path $m.partial '.plastic/plastic.fullupdate')); configuration=[ordered]@{mode=$Mode;selective=[bool]$Selective;private=[bool]$Private;load=$Load;unrelatedDelete=[bool]$UnrelatedDelete;fullWorkspace=[bool]$FullWorkspace}}); Save
    Native $m.partial @('status','--short','--machinereadable') | Out-Null
    Native $m.partial @('ls',$m.partial,'-R','--xml','--encoding=utf-8') | Out-Null
}
if (Test-Path -LiteralPath $result) { throw 'Use a fresh fixture for every probe' }
if ((Get-ChildItem -LiteralPath $m.producer -Force | Where-Object {$_.Name -ne '.plastic'}).Count) { throw 'Producer must start empty' }
New-Item -ItemType Directory -Path (Join-Path $m.producer 'old/sub'),(Join-Path $m.producer 'outside') | Out-Null
WriteFile 'old/a.txt' 'base a'; WriteFile 'old/sub/b.txt' 'base b'; WriteFile 'old/sub/unloaded.txt' 'unloaded inside'
WriteFile 'outside/loaded.txt' 'base outside'; WriteFile 'outside/unloaded.txt' 'unloaded outside'; WriteFile 'sentinel.txt' 'base sentinel'
if ($UnrelatedDelete) { New-Item -ItemType Directory -Path (Join-Path $m.producer 'other-gone') | Out-Null; WriteFile 'other-gone/keep.txt' 'unrelated incoming deletion' }
Native $m.producer @('add',$m.producer,'-R') | Out-Null
Native $m.producer @('checkin',$m.producer,'--all','-c=Partial directory native probe base') | Out-Null
Native $m.partial @('partial','update',$m.partial,'--dontmerge','--report') | Out-Null
if (!$FullWorkspace) { Native $m.partial @('partial','configure','-/outside/unloaded.txt') | Out-Null }
if ($Selective) { Native $m.partial @('partial','configure','-/old/sub/unloaded.txt') | Out-Null }
WriteFile 'old/a.txt' 'local edit a' $m.partial
WriteFile 'sentinel.txt' 'unrelated pending sentinel' $m.partial
if ($Private) {
    WriteFile 'old/private.txt' 'private old root' $m.partial
    New-Item -ItemType Directory -Path (Join-Path $m.partial 'old/private-dir') | Out-Null
    WriteFile 'old/private-dir/private.txt' 'private nested' $m.partial
}
if ($Mode -eq 'Move') {
    Native $m.producer @('move',(Join-Path $m.producer 'old'),(Join-Path $m.producer 'new')) | Out-Null
    WriteFile 'new/a.txt' 'incoming a'; WriteFile 'new/sub/b.txt' 'incoming b'
} else { Native $m.producer @('remove',(Join-Path $m.producer 'old')) | Out-Null }
WriteFile 'outside/loaded.txt' 'incoming outside'
if ($UnrelatedDelete) { Native $m.producer @('remove',(Join-Path $m.producer 'other-gone')) | Out-Null }
Native $m.producer @('checkin',$m.producer,'--all',"-c=Partial directory native probe $Mode") | Out-Null
Snapshot 'Before decision'
Copy-Item -LiteralPath (Join-Path $m.partial 'old/a.txt') -Destination (Join-Path $run 'original-local-a.backup')
Native $m.partial @('partial','undo',(Join-Path $m.partial 'old/a.txt')) | Out-Null
Native $m.partial @('partial','configure','-/old') -AllowFailure | Out-Null
Snapshot 'After exact directory unload'
if ($Mode -eq 'Move') {
    if ($Load -eq 'Directory') { Native $m.partial @('partial','configure','+/new') -AllowFailure | Out-Null }
    else { Native $m.partial @('partial','configure','+/new/a.txt','+/new/sub/b.txt') -AllowFailure | Out-Null }
    Snapshot 'After incoming load'
}
Write-Output $result
