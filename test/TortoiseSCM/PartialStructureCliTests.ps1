# Public executable structure workflow. Writes are restricted to a dedicated autotest fixture.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release\TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe'
)
$ErrorActionPreference = 'Stop'
$m = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$utf8 = New-Object Text.UTF8Encoding($false)
$events = New-Object 'Collections.Generic.List[object]'
$script:assertions = 0
$settings = Join-Path $m.runDirectory 'partial-structure-cli-settings.xml'
function Assert([bool]$Condition, [string]$Description) {
    $script:assertions++; $events.Add([pscustomobject]@{description=$Description;success=$Condition})
    if (-not $Condition) { throw $Description }; Write-Host "PASS: $Description"
}
function Quote([string]$Value) { return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }
function Run([string]$File, [string]$Working, [string[]]$Arguments, [int]$Expected=0) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=[IO.Path]::GetFullPath($File); $start.WorkingDirectory=$Working
    $start.Arguments=($Arguments | ForEach-Object { Quote $_ }) -join ' '
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $process=[Diagnostics.Process]::Start($start)
    try {
        $output=$process.StandardOutput.ReadToEndAsync(); $errorOutput=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'CLI integration subprocess timed out' }
        $text=$output.GetAwaiter().GetResult(); $errorText=$errorOutput.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{file=$File;arguments=$Arguments;cwd=$Working;exitCode=$process.ExitCode;output=$text;error=$errorText})
        if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode): $text $errorText" }; return $text
    } finally { $process.Dispose() }
}
function Invoke-TestCli([string]$Command,[string]$Path,[string[]]$Extra=@(),[int]$Expected=0) {
    $json = Run $Executable $m.runDirectory (@('--cli','--json','--command',$Command,'--path',$Path,'--cm',$CmPath,'--settings-file',$settings) + $Extra) $Expected | ConvertFrom-Json
    if ($json.exitCode -ne $Expected -or $json.success -ne ($Expected -eq 0)) { throw 'CLI JSON status disagrees with process status' }; return $json
}
function Native([string]$Working,[string[]]$Arguments) {
    if (-not [IO.Path]::GetFullPath($Working).StartsWith([IO.Path]::GetFullPath($m.runDirectory)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture working directory' }
    return Run $CmPath $Working $Arguments
}
function Write-New([string]$Path,[string]$Text) {
    $stream=New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
    try { $bytes=$utf8.GetBytes($Text); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
}
function Snapshot {
    $metadata=[IO.Path]::GetFullPath((Join-Path $m.partial '.plastic'))+'\'
    return (@(Get-ChildItem -LiteralPath $m.partial -Recurse -File -Force | Where-Object { -not $_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($m.partial.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
function Preview-One([string]$Item,[string]$Resolution) {
    $preview=Invoke-TestCli 'partial-structure-preview' $m.partial
    $matches=@($preview.data.conflicts | Where-Object item -eq $Item)
    Assert ($matches.Count -eq 1 -and $matches[0].resolutionOptions -contains $Resolution) "Public preview offers $Resolution for $Item"
    return $matches[0]
}
function Prepare-One([string]$Item) {
    $before=Snapshot
    $prepared=Invoke-TestCli 'partial-structure-prepare' $m.partial @('--item',$Item,'--yes')
    $status=Invoke-TestCli 'partial-structure-status' $m.partial
    Assert ($status.data.sessionId -eq $prepared.data.sessionId -and $status.data.ready -and -not $status.data.applying -and (Test-Path -LiteralPath $status.data.recoveryDirectory)) 'Separate executable process resumes ready structure session with recovery directory'
    Assert ((Snapshot) -ceq $before) 'Structure preparation preserves all working file bytes'
    return $status
}
function Finish-Session {
    Assert ($null -eq (Invoke-TestCli 'partial-structure-status' $m.partial).data.sessionId) 'Completed public structure workflow detaches session'
}
function Publish-Base([string]$Name,[string]$Text) {
    Native $m.producer @('update',$m.producer,'--dontmerge') | Out-Null
    $path=Join-Path $m.producer $Name; Write-New $path $Text
    Native $m.producer @('add',$path) | Out-Null
    Native $m.producer @('checkin',$path,'-c=Partial structure CLI base') | Out-Null
    Native $m.partial @('partial','configure',('+/' + $Name)) | Out-Null
}
$success=$false
try {
    Assert ($m.complete -and $m.partialReady -and $m.branch.StartsWith('/main/tortoisescm-autotest-')) 'Structure CLI fixture is an isolated completed test branch'
    foreach ($workspace in @($m.producer,$m.partial,$m.consumer)) {
        Assert ([IO.Path]::GetFullPath($workspace).StartsWith([IO.Path]::GetFullPath($m.runDirectory)+'\',[StringComparison]::OrdinalIgnoreCase) -and [IO.File]::ReadAllText((Join-Path $workspace '.plastic\plastic.selector')).Contains($m.branch)) 'Structure fixture workspace and selector are isolated'
    }
    $prefix='structure-cli-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $unrelated=Join-Path $m.partial ($prefix+'-unrelated.txt'); Write-New $unrelated "unrelated private bytes`n"
    $selector=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic\plastic.selector'))

    # Two independently added items at the same path: preserve both via an explicit rename.
    $name=$prefix+'-'+[char]0x540C+[char]0x540D+'.txt'; $item='/'+$name; $local=Join-Path $m.partial $name; $producer=Join-Path $m.producer $name
    Write-New $local "local added bytes`n"; Native $m.partial @('partial','add',$local) | Out-Null
    Write-New $producer "server added bytes`n"; Native $m.producer @('add',$producer) | Out-Null
    Native $m.producer @('checkin',$producer,'-c=Partial structure same-name server addition') | Out-Null
    Preview-One $item 'rename' | Out-Null
    Invoke-TestCli 'partial-structure-prepare' $m.partial @('--item',$item) 2 | Out-Null
    $prepared=Prepare-One $item; $before=Snapshot
    Invoke-TestCli 'checkin' $local @('--yes','--comment','must not publish prepared structure') 2 | Out-Null
    Invoke-TestCli 'undo' $local @('--yes') 2 | Out-Null
    Invoke-TestCli 'update' $m.partial @('--yes') 2 | Out-Null
    Assert ((Snapshot) -ceq $before) 'Prepared structure session blocks ordinary checkin undo and update without changing bytes'
    Invoke-TestCli 'partial-structure-cancel' $m.partial @('--yes') | Out-Null
    Assert ((Snapshot) -ceq $before -and (Test-Path -LiteralPath $prepared.data.recoveryDirectory)) 'Cancel retains recovery backups and all working bytes'
    Finish-Session
    Prepare-One $item | Out-Null
    $renamed=$prefix+'-local-kept.txt'; $renamedPath=Join-Path $m.partial $renamed
    Invoke-TestCli 'partial-structure-resolve' $m.partial @('--resolution','rename','--rename',$renamed,'--yes') | Out-Null
    Assert ([IO.File]::ReadAllText($local) -ceq "server added bytes`n" -and [IO.File]::ReadAllText($renamedPath) -ceq "local added bytes`n") 'Rename resolution preserves incoming and locally added bytes at distinct paths'
    Invoke-TestCli 'checkin' $renamedPath @('--yes','--comment','publish preserved local add under reviewed name') | Out-Null
    Finish-Session
    Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
    Assert ([IO.File]::ReadAllText((Join-Path $m.consumer $name)) -ceq "server added bytes`n" -and [IO.File]::ReadAllText((Join-Path $m.consumer $renamed)) -ceq "local added bytes`n") 'Independent consumer receives both separately preserved additions'

    # Server deleted a locally edited file: preserving it is a deliberate new addition.
    $name=$prefix+'-incoming-delete.txt'; $item='/'+$name; Publish-Base $name "base deletion candidate`n"
    $local=Join-Path $m.partial $name; $producer=Join-Path $m.producer $name
    [IO.File]::WriteAllText($local,"keep edited local after deletion`n",$utf8)
    Native $m.producer @('remove',$producer) | Out-Null
    Native $m.producer @('checkin',$producer,'-c=Partial structure server deletion') | Out-Null
    Preview-One $item 'keep-local' | Out-Null; Prepare-One $item | Out-Null
    Invoke-TestCli 'partial-structure-resolve' $m.partial @('--resolution','keep-local','--yes') | Out-Null
    $pending=Invoke-TestCli 'status' $m.partial
    Assert ([IO.File]::ReadAllText($local) -ceq "keep edited local after deletion`n" -and @($pending.data.entries | Where-Object { $_.path -eq $local -and $_.status -eq 'AD' }).Count -eq 1) 'Incoming deletion keep-local preserves bytes as a pending re-add'
    Invoke-TestCli 'checkin' $local @('--yes','--comment','explicitly re-add locally preserved deleted item') | Out-Null
    Finish-Session; Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
    Assert ([IO.File]::ReadAllText((Join-Path $m.consumer $name)) -ceq "keep edited local after deletion`n") 'Consumer receives explicit re-add after incoming deletion'

    # Local deletion versus server content change: discard deletion and take incoming.
    $name=$prefix+'-local-delete.txt'; $item='/'+$name; Publish-Base $name "local delete base`n"
    $local=Join-Path $m.partial $name; $producer=Join-Path $m.producer $name
    Native $m.partial @('partial','remove',$local) | Out-Null
    [IO.File]::WriteAllText($producer,"incoming survives local deletion`n",$utf8)
    Native $m.producer @('checkin',$producer,'-c=Partial structure incoming over local delete') | Out-Null
    Preview-One $item 'take-incoming' | Out-Null; Prepare-One $item | Out-Null
    Invoke-TestCli 'partial-structure-resolve' $m.partial @('--resolution','take-incoming','--yes') | Out-Null
    Assert ([IO.File]::ReadAllText($local) -ceq "incoming survives local deletion`n") 'Take-incoming restores exact server bytes after local deletion'
    Finish-Session

    # A pure cross-directory local move keeps its destination and incorporates server content.
    $name=$prefix+'-move-source.txt'; Publish-Base $name "move base`n"
    $destinationDirectory=$prefix+'-destination'
    $producerDirectory=Join-Path $m.producer $destinationDirectory
    New-Item -ItemType Directory -Path $producerDirectory | Out-Null
    Write-New (Join-Path $producerDirectory 'untouched.txt') "destination sibling`n"
    Native $m.producer @('add',$producerDirectory,'-R') | Out-Null
    Native $m.producer @('checkin',$producerDirectory,'-c=Loaded destination directory for cross-directory CLI move') | Out-Null
    Native $m.partial @('partial','configure',('+/'+$destinationDirectory)) | Out-Null
    $loadRules=[IO.File]::ReadAllText((Join-Path $m.partial '.plastic\plastic.fullycheckeddirectories'))
    $movedName=$destinationDirectory+'/move-destination.txt'; $item='/'+$movedName
    $old=Join-Path $m.partial $name; $local=Join-Path $m.partial $movedName; $producer=Join-Path $m.producer $name
    Native $m.partial @('partial','move',$old,$local) | Out-Null
    [IO.File]::WriteAllText($producer,"incoming content follows reviewed local move`n",$utf8)
    Native $m.producer @('checkin',$producer,'-c=Partial structure incoming during local move') | Out-Null
    Preview-One $item 'keep-local' | Out-Null; Prepare-One $item | Out-Null
    Invoke-TestCli 'partial-structure-resolve' $m.partial @('--resolution','keep-local','--yes') | Out-Null
    Assert (-not (Test-Path -LiteralPath $old) -and [IO.File]::ReadAllText($local) -ceq "incoming content follows reviewed local move`n") 'Keep-local retains move destination while using incoming content'
    Invoke-TestCli 'checkin' $local @('--yes','--comment','publish reviewed local move with incoming content') | Out-Null
    Finish-Session; Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
    Assert (-not (Test-Path -LiteralPath (Join-Path $m.consumer $name)) -and [IO.File]::ReadAllText((Join-Path $m.consumer $movedName)) -ceq "incoming content follows reviewed local move`n") 'Consumer receives reviewed move and exact incoming content'
    Assert ([IO.File]::ReadAllText((Join-Path $m.partial ($destinationDirectory+'/untouched.txt'))) -ceq "destination sibling`n" -and [IO.File]::ReadAllText((Join-Path $m.partial '.plastic\plastic.fullycheckeddirectories')) -ceq $loadRules) 'Cross-directory resolution preserves sibling bytes and load configuration'

    # Another cross-directory move explicitly chooses a repository path for its final name.
    $name=$prefix+'-rename-source.txt'; Publish-Base $name "rename base`n"
    $movedName=$destinationDirectory+'/rename-destination.txt'; $item='/'+$movedName
    $old=Join-Path $m.partial $name; $local=Join-Path $m.partial $movedName
    Native $m.partial @('partial','move',$old,$local) | Out-Null
    [IO.File]::WriteAllText((Join-Path $m.producer $name),"incoming renamed content`n",$utf8)
    Native $m.producer @('checkin',(Join-Path $m.producer $name),'-c=Incoming content for explicit cross-directory rename') | Out-Null
    Preview-One $item 'rename' | Out-Null; Prepare-One $item | Out-Null
    $before=Snapshot
    Invoke-TestCli 'partial-structure-resolve' $m.partial @('--resolution','rename','--rename','/missing-directory/file.txt','--yes') 2 | Out-Null
    Assert ((Snapshot) -ceq $before -and (Invoke-TestCli 'partial-structure-status' $m.partial).data.ready) 'Unloaded or missing rename parent is rejected before changing files'
    $finalName=$destinationDirectory+'/reviewed-name.txt'; $finalPath=Join-Path $m.partial $finalName
    Invoke-TestCli 'partial-structure-resolve' $m.partial @('--resolution','rename','--rename',('/'+$finalName),'--yes') | Out-Null
    Assert (-not (Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $local) -and [IO.File]::ReadAllText($finalPath) -ceq "incoming renamed content`n") 'Repository-path rename keeps the reviewed cross-directory destination and incoming bytes'
    Finish-Session
    Invoke-TestCli 'checkin' $finalPath @('--yes','--comment','publish reviewed cross-directory name') | Out-Null
    Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
    Assert (-not (Test-Path -LiteralPath (Join-Path $m.consumer $name)) -and [IO.File]::ReadAllText((Join-Path $m.consumer $finalName)) -ceq "incoming renamed content`n") 'Consumer receives explicit cross-directory rename through the public CLI'
    # Incoming same-directory and cross-directory moves use exact old/new file paths.
    foreach ($decision in @('keep-local','take-incoming')) {
        $name=$prefix+'-incoming-'+$decision+'.txt'; Publish-Base $name "incoming move base`n"
        $newName=if ($decision -eq 'keep-local') { $prefix+'-incoming-moved.txt' } else { $destinationDirectory+'/incoming-moved.txt' }
        $old=Join-Path $m.partial $name; $newLocal=Join-Path $m.partial $newName
        [IO.File]::WriteAllText($old,"reviewed local incoming move`n",$utf8)
        Native $m.producer @('move',(Join-Path $m.producer $name),(Join-Path $m.producer $newName)) | Out-Null
        [IO.File]::WriteAllText((Join-Path $m.producer $newName),"server moved content`n",$utf8)
        Native $m.producer @('checkin',(Join-Path $m.producer $newName),'-c=Server file move for public CLI') | Out-Null
        Preview-One ('/'+$name) $decision | Out-Null
        $prepared=Prepare-One ('/'+$name)
        Invoke-TestCli 'partial-structure-resolve' $m.partial @('--resolution',$decision,'--yes') | Out-Null
        $expected=if ($decision -eq 'keep-local') { "reviewed local incoming move`n" } else { "server moved content`n" }
        Assert (-not (Test-Path -LiteralPath $old) -and [IO.File]::ReadAllText($newLocal) -ceq $expected) "Incoming move $decision follows the server path with reviewed bytes"
        Assert ([IO.File]::ReadAllText((Join-Path $prepared.data.recoveryDirectory 'local.bin')) -ceq "reviewed local incoming move`n") 'Incoming move retains original local backup after success'
        Finish-Session
        if ($decision -eq 'keep-local') { Invoke-TestCli 'checkin' $newLocal @('--yes','--comment','publish local content at incoming move destination') | Out-Null }
        Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
        Assert (-not (Test-Path -LiteralPath (Join-Path $m.consumer $name)) -and [IO.File]::ReadAllText((Join-Path $m.consumer $newName)) -ceq $expected) "Consumer receives incoming move $decision outcome"
    }
    Assert ([IO.File]::ReadAllText($unrelated) -ceq "unrelated private bytes`n" -and [IO.File]::ReadAllText((Join-Path $m.partial '.plastic\plastic.selector')) -ceq $selector -and (Invoke-TestCli 'status' $m.partial).data.workspace.isPartial) 'Structure roundtrip preserves unrelated bytes selector and Partial mode'
    $success=$true; Write-Output "PASS: $script:assertions real Partial structure CLI assertions"
} finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'partial-structure-cli-results.json'),([pscustomobject]@{success=$success;assertions=$script:assertions;events=$events.ToArray()} | ConvertTo-Json -Depth 20),$utf8)
}
