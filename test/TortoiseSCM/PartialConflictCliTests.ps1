# Real public CLI roundtrip. All writes stay on a dedicated autotest fixture branch.
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
    $json = Run $Executable $m.runDirectory (@('--cli','--json','--command',$Command,'--path',$Path,'--cm',$CmPath,'--settings-file',(Join-Path $m.runDirectory 'partial-cli-settings.xml')) + $Extra) $Expected | ConvertFrom-Json
    if ($json.exitCode -ne $Expected -or $json.success -ne ($Expected -eq 0)) { throw 'CLI JSON status disagrees with process status' }; return $json
}
function Native([string]$Working,[string[]]$Arguments) {
    if (-not [IO.Path]::GetFullPath($Working).StartsWith([IO.Path]::GetFullPath($m.runDirectory)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture working directory' }
    return Run $CmPath $Working $Arguments
}
$success=$false
try {
    Assert ($m.branch.StartsWith('/main/tortoisescm-autotest-')) 'CLI fixture is an isolated test branch'
    $name='partial-cli-'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.txt'; $item='/'+$name
    $producer=Join-Path $m.producer $name; $local=Join-Path $m.partial $name
    Native $m.producer @('update',$m.producer,'--dontmerge') | Out-Null
    [IO.File]::WriteAllText($producer,"base`n",$utf8)
    Native $m.producer @('add',$producer) | Out-Null; Native $m.producer @('checkin',$producer,'-c=Partial CLI base') | Out-Null
    Native $m.partial @('partial','configure',('+'+$item)) | Out-Null
    [IO.File]::WriteAllText($local,"local`n",$utf8); [IO.File]::WriteAllText($producer,"incoming`n",$utf8)
    Native $m.producer @('checkin',$producer,'-c=Partial CLI incoming') | Out-Null
    $preview=Invoke-TestCli 'partial-conflicts' $m.partial
    Assert (@($preview.data.conflicts | Where-Object item -eq $item).Count -eq 1) 'Real executable reports the incoming conflict in JSON'
    Invoke-TestCli 'partial-conflict-prepare' $m.partial @('--item',$item) 2 | Out-Null
    $prepared=Invoke-TestCli 'partial-conflict-prepare' $m.partial @('--item',$item,'--yes')
    Assert ([IO.File]::ReadAllText($prepared.data.basePath) -eq "base`n" -and [IO.File]::ReadAllText($prepared.data.localPath) -eq "local`n" -and [IO.File]::ReadAllText($prepared.data.remotePath) -eq "incoming`n") 'CLI exports exact base/local/incoming files'
    $session=Invoke-TestCli 'partial-conflict-status' $m.partial
    Assert ($session.data.ready -and -not $session.data.applying -and (Test-Path -LiteralPath $session.data.recoveryDirectory)) 'CLI restart returns ready state and recoveryDirectory'
    Invoke-TestCli 'checkin' $local @('--yes','--comment','must reject unresolved') 2 | Out-Null
    [IO.File]::WriteAllText($prepared.data.resultPath,"reviewed CLI result`n",$utf8)
    Invoke-TestCli 'partial-conflict-resolve' $m.partial @('--item',$item,'--result',$prepared.data.resultPath,'--yes') | Out-Null
    $resolved=Invoke-TestCli 'partial-conflict-status' $m.partial
    Assert ((@($resolved.data.conflicts | Where-Object item -eq $item)[0]).resolved -and [IO.File]::ReadAllText($local) -eq "reviewed CLI result`n") 'CLI explicitly applies and persists reviewed resolution'
    $pending=Invoke-TestCli 'status' $m.partial
    Assert (@($pending.data.entries | Where-Object path -eq $local).Count -eq 1) 'Resolved file stays pending before explicit checkin'
    Invoke-TestCli 'checkin' $local @('--yes','--comment','publish Partial CLI reviewed result') | Out-Null
    $completed=Invoke-TestCli 'partial-conflict-status' $m.partial
    Assert ($null -eq $completed.data.sessionId) 'CLI checkin retires its completed session'
    Native $m.consumer @('update',$m.consumer,'--dontmerge') | Out-Null
    Assert ([IO.File]::ReadAllText((Join-Path $m.consumer $name)) -eq "reviewed CLI result`n") 'Independent consumer receives CLI reviewed bytes'
    Assert ((Invoke-TestCli 'status' $m.partial).data.workspace.isPartial) 'CLI roundtrip retains Partial workspace mode'
    $success=$true; Write-Output "PASS: $script:assertions real Partial CLI assertions"
} finally {
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'partial-cli-results.json'),([pscustomobject]@{success=$success;assertions=$script:assertions;events=$events.ToArray()} | ConvertTo-Json -Depth 20),$utf8)
}
