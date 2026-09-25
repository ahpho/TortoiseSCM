# GPL-2.0-or-later. Deleted file identity after an accepted directory move.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ManifestPath,
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe')
$ErrorActionPreference = 'Stop'
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $ManifestPath
$utf8 = New-Object Text.UTF8Encoding($false)
$events = [Collections.Generic.List[object]]::new()
$result = Join-Path $m.runDirectory 'partial-directory-deleted-identity.json'
$file = Join-Path $m.partial 'new/sub/unloaded.txt'
if (!(Test-Path -LiteralPath (Join-Path $m.runDirectory 'partial-directory-native-probe.json')) -or !(Test-Path -LiteralPath $file) -or (Test-Path -LiteralPath $result)) { throw 'Use an unused completed native directory move probe with the unchanged child loaded' }
function Native([string[]]$arguments,[switch]$AllowFailure) {
    Push-Location -LiteralPath $m.partial
    try {
        $saved=$ErrorActionPreference; $ErrorActionPreference='Continue'
        $lines=@(& $CmPath @arguments 2>&1); $code=$LASTEXITCODE; $ErrorActionPreference=$saved
        $output=($lines | ForEach-Object {"$_"}) -join "`n"
        $events.Add([ordered]@{arguments=$arguments;exitCode=$code;output=$output})
        [IO.File]::WriteAllText($result,($events.ToArray()|ConvertTo-Json -Depth 12),$utf8)
        if($code -ne 0 -and !$AllowFailure){throw "Native deleted identity probe failed: $output"}; return $output
    } finally {Pop-Location}
}
function ReadIdentity([string]$phase) {
    $events.Add([ordered]@{phase=$phase})
    Native @('status','--short','--machinereadable') | Out-Null
    Native @('status','--xml','--encoding=utf-8') | Out-Null
    Native @('ls',$file,'--xml','--encoding=utf-8') -AllowFailure | Out-Null
    Native @('ls',$m.partial,'-R','--xml','--encoding=utf-8') -AllowFailure | Out-Null
    Native @('fileinfo',$file,'--fields=RevisionChangeset,Status,Type,IsUnderXlink','--xml','--encoding=utf-8') -AllowFailure | Out-Null
    [xml]$parent = Native @('fileinfo',(Split-Path -Parent $file),'--fields=RevisionChangeset,Status,Type,RepSpec,ServerPath','--xml','--encoding=utf-8')
    $parentRevision = [long]$parent.FileInfos.FileInfo.RevisionChangeset
    if ($parentRevision -ge 0) { Native @('ls','/new/sub/unloaded.txt',("--tree=cs:"+$parentRevision+'@'+$m.repository),'--xml','--encoding=utf-8') -AllowFailure | Out-Null }
}
Copy-Item -LiteralPath $file -Destination (Join-Path $m.runDirectory 'deleted-identity-original.backup')
ReadIdentity 'before'
Native @('partial','remove',$file) | Out-Null
ReadIdentity 'native-DE'
Native @('partial','undo',$file) | Out-Null
if (![IO.Path]::GetFullPath($file).StartsWith($m.partial+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe delete path'}
[IO.File]::Delete($file)
ReadIdentity 'filesystem-LD'
Native @('partial','undo',$file) | Out-Null
ReadIdentity 'restored'
Write-Output $result
