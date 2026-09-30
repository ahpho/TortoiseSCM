# One real, atomic bulk check-in through the documented client REST API.
# Creates a dedicated empty autotest branch. Never submits or retries in an
# existing workspace. Leaves the fixture and raw evidence for inspection.
[CmdletBinding()]
param(
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [string]$ReferenceWorkspace = '',
    [int]$FileCount = 194,
    [int]$Port = 19092,
    [switch]$RunCheckin
)

$ErrorActionPreference = 'Stop'
if (-not $RunCheckin) { throw 'Supply -RunCheckin to authorize this isolated live test.' }
if ($FileCount -lt 1 -or $FileCount -gt 1000) { throw 'FileCount must be between 1 and 1000.' }
$utf8 = [Text.UTF8Encoding]::new($false)
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$manifestPath = & (Join-Path $PSScriptRoot 'New-TestWorkspace.ps1') -CmPath $CmPath -ReferenceWorkspace $ReferenceWorkspace
$manifest = [IO.File]::ReadAllText([string]$manifestPath, $utf8) | ConvertFrom-Json
$runRoot = [IO.Path]::GetFullPath([string]$manifest.runDirectory)
$workspace = [IO.Path]::GetFullPath([string]$manifest.partial)
$qaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\qa'))
if (-not $manifest.complete -or -not $manifest.partialReady -or
    -not $runRoot.StartsWith($qaRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not $workspace.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [string]$manifest.branch -notmatch '^/main/tortoisescm-autotest-') {
    throw 'Refusing an incomplete or non-isolated fixture.'
}
$resultPath = Join-Path $runRoot 'checkin-api-live-results.json'
$record = [ordered]@{
    startedAt = (Get-Date).ToString('o')
    manifestPath = [string]$manifestPath
    branch = [string]$manifest.branch
    workspace = $workspace
    pathCount = $FileCount
    apiPort = $Port
    requestCount = 0
    native = @()
    responses = @()
    pendingBefore = $null
    pendingAfter = $null
    branchChangesetsBefore = ''
    branchChangesetsAfter = ''
    serverFileCountAfter = $null
    apiTerminalStatus = $null
    elapsedMs = $null
    success = $false
    error = $null
}
function Save-Record {
    [IO.File]::WriteAllText($resultPath, ($record | ConvertTo-Json -Depth 20), $utf8)
}
function Quote-Argument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}
function New-Start([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $CmPath
    $start.Arguments = (@($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $start.WorkingDirectory = $workspace
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    return $start
}
function Decode-Raw([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    try { return $strictUtf8.GetString($bytes) }
    catch [Text.DecoderFallbackException] { return [Text.Encoding]::Default.GetString($bytes) }
}
function Start-RawCapture($Process, [string]$Prefix) {
    $directory = Join-Path $runRoot 'raw'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $stdoutPath = Join-Path $directory ($Prefix + '-stdout.bin')
    $stderrPath = Join-Path $directory ($Prefix + '-stderr.bin')
    $stdoutFile = [IO.File]::Create($stdoutPath)
    $stderrFile = [IO.File]::Create($stderrPath)
    return [pscustomobject]@{
        stdoutPath = $stdoutPath; stderrPath = $stderrPath
        stdoutFile = $stdoutFile; stderrFile = $stderrFile
        stdoutCopy = $Process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
        stderrCopy = $Process.StandardError.BaseStream.CopyToAsync($stderrFile)
    }
}
function Complete-RawCapture($Capture) {
    try {
        [void]$Capture.stdoutCopy.GetAwaiter().GetResult()
        [void]$Capture.stderrCopy.GetAwaiter().GetResult()
    }
    finally { $Capture.stdoutFile.Dispose(); $Capture.stderrFile.Dispose() }
    return [pscustomobject]@{
        stdoutRaw = $Capture.stdoutPath; stderrRaw = $Capture.stderrPath
        stdout = Decode-Raw $Capture.stdoutPath; stderr = Decode-Raw $Capture.stderrPath
    }
}
function Native([string[]]$Arguments) {
    $process = [Diagnostics.Process]::Start((New-Start $Arguments))
    $capture = Start-RawCapture $process ('native-{0:D3}' -f $record.native.Count)
    try {
        $process.StandardInput.Close()
        $timedOut = -not $process.WaitForExit(30000)
        if ($timedOut) { $process.Kill(); $process.WaitForExit() }
        $captured = Complete-RawCapture $capture
        $capture = $null
        $event = [ordered]@{
            arguments=$Arguments; exitCode=$process.ExitCode; timedOut=$timedOut
            stdout=$captured.stdout; stderr=$captured.stderr
            stdoutRaw=$captured.stdoutRaw; stderrRaw=$captured.stderrRaw
        }
        $record.native += $event
        Save-Record
        if ($timedOut) { throw 'Read/preparation cm command timed out; raw output was preserved.' }
        if ($process.ExitCode -ne 0) { throw ('cm failed: '+$event.stdout+$event.stderr) }
        return [string]$event.stdout
    }
    finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if ($capture) { Complete-RawCapture $capture | Out-Null }
        $process.Dispose()
    }
}
Add-Type -AssemblyName System.Net.Http
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(15)
$api = $null
$apiCapture = $null
$timer = $null
function Api-Get([string]$Route, [string]$Label) {
    $response = $client.GetAsync('http://127.0.0.1:'+$Port+'/api/v1/'+$Route).GetAwaiter().GetResult()
    $raw = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $record.responses += [ordered]@{ at=(Get-Date).ToString('o'); method='GET'; label=$Label; statusCode=[int]$response.StatusCode; body=$raw }
    Save-Record
    if (-not $response.IsSuccessStatusCode) { throw ('API GET '+$Route+' failed: '+$raw) }
    return ($raw | ConvertFrom-Json)
}
try {
    $selector = Native @('showselector')
    if ($selector -notmatch [regex]::Escape([string]$manifest.branch)) { throw 'Selector is not the dedicated branch.' }
    $record.branchChangesetsBefore = Native @('find','changeset',("where branch = '"+$manifest.branch+"' order by changesetid asc"),'--format={changesetid}','--nototal')
    $paths = @(0..($FileCount-1) | ForEach-Object { Join-Path $workspace ('api-file-{0:D3}.txt' -f $_) })
    for ($i=0; $i -lt $paths.Count; $i++) {
        [IO.File]::WriteAllText($paths[$i], ('REST API bulk fixture '+$manifest.runId+' file '+$i+"`r`n"), $utf8)
    }
    Native (@('partial','add')+$paths) | Out-Null
    $record.pendingBefore = Native @('status','--noheader','--short','--machinereadable')
    Save-Record
    $api = [Diagnostics.Process]::Start((New-Start @('api', ('--port='+$Port))))
    $apiCapture = Start-RawCapture $api 'api-server'
    $ready = $false
    for ($i=0; $i -lt 30; $i++) {
        if ($api.HasExited) { throw 'cm api exited before becoming ready.' }
        try { $workspaces = Api-Get 'wkspaces' 'api-ready'; $ready=$true; break }
        catch { if ($i -eq 29) { throw }; Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'Loopback API did not become ready.' }
    $apiWorkspace = @($workspaces | Where-Object { $_.path.TrimEnd('\','/') -ieq $workspace.TrimEnd('\','/') })
    if ($apiWorkspace.Count -ne 1 -or $apiWorkspace[0].name -notmatch '^tscm-integration-') { throw 'API fixture workspace could not be verified.' }
    $route = 'wkspaces/'+[Uri]::EscapeDataString([string]$apiWorkspace[0].name)+'/checkin'
    $before = Api-Get $route 'checkin-before'
    if ($before.status -ne 'Not running') { throw 'Refusing to submit over an existing operation.' }
    $payload = [ordered]@{ paths=$paths; comment=('QA public REST atomic '+$FileCount+' files '+$manifest.runId); recurse=$true }
    $json = $payload | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $runRoot 'checkin-api-request.json'), $json, $utf8)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $record.requestCount = 1
    Save-Record
    $content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    $response = $client.PostAsync('http://127.0.0.1:'+$Port+'/api/v1/'+$route, $content).GetAwaiter().GetResult()
    $raw = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $runRoot 'checkin-api-post-response.json'), $raw, $utf8)
    $record.responses += [ordered]@{ at=(Get-Date).ToString('o'); method='POST'; label='submit-once'; statusCode=[int]$response.StatusCode; body=$raw }
    Save-Record
    if (-not $response.IsSuccessStatusCode) { throw ('API POST failed: '+$raw) }
    do {
        $status = Api-Get $route 'checkin-poll'
        $record.apiTerminalStatus = $status
        $record.elapsedMs = $timer.ElapsedMilliseconds
        Save-Record
        if ($status.status -match '^(Failed|Error|Finished|Completed|Successful|Success|Succeeded|Not running)$') { break }
        if ($timer.Elapsed.TotalSeconds -ge 180) { throw 'API operation did not complete within 180 seconds; do not retry.' }
        Start-Sleep -Seconds 1
    } while ($true)
}
catch {
    $record.error = $_.Exception.ToString()
}
finally {
    if ($timer) { $record.elapsedMs = $timer.ElapsedMilliseconds }
    try {
        $record.pendingAfter = Native @('status','--noheader','--short','--machinereadable')
        $record.branchChangesetsAfter = Native @('find','changeset',("where branch = '"+$manifest.branch+"' order by changesetid asc"),'--format={changesetid}','--nototal')
        $serverTree = Native @('ls',('--tree=br:'+$manifest.branch+'@'+$manifest.repository),'-R','--format={type}|{path}')
        [IO.File]::WriteAllText((Join-Path $runRoot 'checkin-api-server-tree.txt'), $serverTree, $utf8)
        $record.serverFileCountAfter = @($serverTree -split '\r?\n' | Where-Object { $_ -match 'api-file-\d{3}\.txt$' }).Count
        $record.success = ($null -eq $record.error -and [string]::IsNullOrWhiteSpace([string]$record.pendingAfter) -and $record.serverFileCountAfter -eq $FileCount -and @($record.branchChangesetsAfter.Trim() -split '\r?\n').Count -eq 1)
    }
    catch { if (-not $record.error) { $record.error=$_.Exception.ToString() } }
    if ($api) {
        if (-not $api.HasExited) { $api.StandardInput.WriteLine(); $api.StandardInput.Close() }
        if (-not $api.WaitForExit(5000)) { $api.Kill(); $api.WaitForExit() }
        if ($apiCapture) {
            $captured = Complete-RawCapture $apiCapture
            [IO.File]::WriteAllText((Join-Path $runRoot 'checkin-api-server.stdout.log'), $captured.stdout, $utf8)
            [IO.File]::WriteAllText((Join-Path $runRoot 'checkin-api-server.stderr.log'), $captured.stderr, $utf8)
        }
        $api.Dispose()
    }
    $client.Dispose()
    Save-Record
    Write-Output $resultPath
}
if (-not $record.success) { throw ('Public REST bulk check-in did not verify success. Evidence: '+$resultPath) }
