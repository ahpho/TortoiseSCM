# Server-backed transport matrix. Every scenario gets an isolated empty branch
# and workspace. Each checkin is attempted once; failures are never retried.
[CmdletBinding()]
param(
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [string]$ReferenceWorkspace = '',
    [ValidateSet('standard', 'partial')][string[]]$Modes = @('standard', 'partial'),
    [ValidateSet('empty', 'identical', 'different')][string[]]$Contents = @('empty', 'identical', 'different'),
    [string]$CheckinClientConfig = '',
    [switch]$RunNetworkProbe
)

$ErrorActionPreference = 'Stop'
if ($CheckinClientConfig) {
    $CheckinClientConfig = [IO.Path]::GetFullPath($CheckinClientConfig)
    if (-not [IO.File]::Exists($CheckinClientConfig)) { throw 'The isolated checkin client configuration does not exist.' }
}
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($ReferenceWorkspace)) { $ReferenceWorkspace = Join-Path (Split-Path -Parent $sourceRoot) 'TestSCM' }
$reference = [IO.Path]::GetFullPath($ReferenceWorkspace)
$selectorPath = Join-Path $reference '.plastic\plastic.selector'
$referenceSelector = [IO.File]::ReadAllText($selectorPath)
$matches = [regex]::Matches($referenceSelector, '(?m)^\s*repository\s+"(?<spec>[^"\r\n]+)"\s*$')
if ($matches.Count -ne 1) { throw 'Reference must select exactly one repository.' }
$repository = $matches[0].Groups['spec'].Value
if ($repository -cnotmatch '^TestSCM@[^\s"\r\n]+$') { throw 'Only the permitted TestSCM test repository may be used.' }
$server = $repository.Substring($repository.IndexOf('@') + 1)
$runId = 'transport-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot ('bin\TortoiseSCM\qa\' + $runId)))
$qaRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'bin\TortoiseSCM\qa')).TrimEnd('\')
if (-not $runRoot.StartsWith($qaRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Run directory escaped QA root.' }
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $runRoot 'raw')) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$script:sequence = 0
$script:record = [ordered]@{
    runId = $runId; startedAt = (Get-Date).ToString('o'); repository = $repository
    runDirectory = $runRoot; referenceWorkspace = $reference; scenarios = @(); commands = @()
    network = $null; checkinClientConfig = $CheckinClientConfig; complete = $false; error = $null
}
$resultsPath = Join-Path $runRoot 'checkin-transport-matrix-results.json'
function Save-Results { [IO.File]::WriteAllText($resultsPath, ($script:record | ConvertTo-Json -Depth 12), $utf8) }
function Quote-Cm([string]$value) {
    if ($value -notmatch '[\s"]') { return $value }
    return '"' + ($value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}
function Decode-Raw([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    try { return $strictUtf8.GetString($bytes) } catch [Text.DecoderFallbackException] { return [Text.Encoding]::Default.GetString($bytes) }
}
function Start-Cm([string[]]$CmArguments, [string]$WorkingDirectory) {
    $script:sequence++
    $prefix = '{0:D3}' -f $script:sequence
    $stdoutPath = Join-Path $runRoot ('raw\' + $prefix + '-stdout.bin')
    $stderrPath = Join-Path $runRoot ('raw\' + $prefix + '-stderr.bin')
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $CmPath; $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Arguments = ($CmArguments | ForEach-Object { Quote-Cm $_ }) -join ' '
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $stdoutFile = [IO.File]::Create($stdoutPath); $stderrFile = [IO.File]::Create($stderrPath)
    return [pscustomobject]@{
        process = $process; timer = $timer; arguments = $CmArguments; cwd = $WorkingDirectory
        stdoutPath = $stdoutPath; stderrPath = $stderrPath; stdoutFile = $stdoutFile; stderrFile = $stderrFile
        stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
        stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
    }
}
function Complete-Cm($pending, [int]$TimeoutSeconds = 180) {
    $remaining = [Math]::Max(0, $TimeoutSeconds * 1000 - $pending.timer.ElapsedMilliseconds)
    $timedOut = -not $pending.process.WaitForExit([int]$remaining)
    if ($timedOut) { $pending.process.Kill(); $pending.process.WaitForExit() }
    try {
        [void]$pending.stdoutCopy.GetAwaiter().GetResult(); [void]$pending.stderrCopy.GetAwaiter().GetResult()
    }
    finally { $pending.stdoutFile.Dispose(); $pending.stderrFile.Dispose(); $pending.timer.Stop() }
    $result = [ordered]@{
        cwd = $pending.cwd; arguments = $pending.arguments
        startedAt = $pending.process.StartTime.ToString('o'); endedAt = $pending.process.ExitTime.ToString('o')
        elapsedMs = [long]($pending.process.ExitTime - $pending.process.StartTime).TotalMilliseconds
        exitCode = $pending.process.ExitCode; timedOut = $timedOut
        stdoutRaw = $pending.stdoutPath; stderrRaw = $pending.stderrPath
        stdout = Decode-Raw $pending.stdoutPath; stderr = Decode-Raw $pending.stderrPath
    }
    $pending.process.Dispose(); $script:record.commands += $result; Save-Results
    return [pscustomobject]$result
}
function Invoke-Cm([string[]]$CmArguments, [string]$WorkingDirectory, [switch]$RequireSuccess) {
    $result = Complete-Cm (Start-Cm $CmArguments $WorkingDirectory)
    if ($RequireSuccess -and ($result.exitCode -ne 0 -or $result.timedOut)) { throw "Setup command failed: $($CmArguments -join ' ') : $($result.stderr) $($result.stdout)" }
    return $result
}
function Server-Changesets([string]$workspace, [string]$branch) {
    return Invoke-Cm @('find', 'changeset', "where branch = '$branch' order by changesetid desc", '--format={changesetid}', '--nototal') $workspace
}
function Parse-Changesets($query) { return @($query.stdout -split '\r?\n' | Where-Object { $_ -match '^\s*\d+\s*$' } | ForEach-Object { [int]$_.Trim() }) }
function Get-CheckinVerification($Before, $After, $Header, $ServerBefore, $ServerAfter, $Checkin) {
    $errors = @()
    foreach ($query in @(@{name='server-before';value=$ServerBefore}, @{name='server-after';value=$ServerAfter},
            @{name='pending-before';value=$Before}, @{name='pending-after';value=$After}, @{name='status-header';value=$Header})) {
        if ($query.value.exitCode -ne 0 -or $query.value.timedOut) { $errors += $query.name + ' query failed' }
    }
    $serverVerified = $ServerBefore.exitCode -eq 0 -and -not $ServerBefore.timedOut -and
        $ServerAfter.exitCode -eq 0 -and -not $ServerAfter.timedOut
    $createdIds = $null
    if ($serverVerified) {
        $beforeIds = Parse-Changesets $ServerBefore; $afterIds = Parse-Changesets $ServerAfter
        $createdIds = @($afterIds | Where-Object { $beforeIds -notcontains $_ })
    }
    $pendingBefore = -1; $pendingAfter = -1
    try { $pendingBefore = ([xml]$Before.stdout).SelectNodes('//Change').Count } catch { }
    try { $pendingAfter = ([xml]$After.stdout).SelectNodes('//Change').Count } catch { }
    if ($pendingBefore -lt 0) { $errors += 'pending-before XML could not be parsed' }
    if ($pendingAfter -lt 0) { $errors += 'pending-after XML could not be parsed' }
    $verified = $errors.Count -eq 0
    return [pscustomobject]@{
        verified = $verified; errors = $errors; serverVerified = $serverVerified
        createdChangesets = $createdIds; pendingBefore = $pendingBefore; pendingAfter = $pendingAfter
        atomicSuccess = $verified -and $Checkin.exitCode -eq 0 -and -not $Checkin.timedOut -and $pendingAfter -eq 0 -and $createdIds.Count -eq 1
    }
}

Save-Results
$baseTree = Invoke-Cm @('ls', "--tree=cs:0@$repository", '-R', '--format={size}|{path}') $runRoot -RequireSuccess
if ($baseTree.stdout.Trim() -ne '0|/') { throw 'Changeset 0 must be empty.' }
$networkPending = $null
if ($RunNetworkProbe) {
    $networkPending = Start-Cm @('iostats', $server, '--serveruploadtest', '--serverdownloadtest', '--nettotalmb=4') $runRoot
}
try {
foreach ($mode in $Modes) {
    foreach ($content in $Contents) {
        $scenarioName = $mode + '-' + $content
        $branch = '/main/tortoisescm-autotest-' + $runId + '-' + $scenarioName
        $workspace = [IO.Path]::GetFullPath((Join-Path $runRoot $scenarioName))
        if (-not $workspace.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Workspace escaped isolated run directory.' }
        $name = 'tscm-' + $runId + '-' + $scenarioName
        [IO.Directory]::CreateDirectory($workspace) | Out-Null
        Write-Host ('Preparing ' + $scenarioName + ': ' + $branch)
        Invoke-Cm @('branch', 'create', "br:$branch@$repository", "--changeset=cs:0@$repository", '-c=QA isolated checkin transport matrix') $runRoot -RequireSuccess | Out-Null
        Invoke-Cm @('workspace', 'create', $name, $workspace, $repository) $runRoot -RequireSuccess | Out-Null
        Invoke-Cm @('switch', "br:$branch@$repository", "--workspace=$workspace") $runRoot -RequireSuccess | Out-Null
        if ($mode -eq 'partial') {
            Invoke-Cm @('partial', 'configure', '-/', '+/') $workspace -RequireSuccess | Out-Null
            Invoke-Cm @('partial', 'update', '.', '--report') $workspace -RequireSuccess | Out-Null
        }
        $actualSelector = Invoke-Cm @('showselector') $workspace -RequireSuccess
        if (-not $actualSelector.stdout.Contains($branch)) { throw 'Scenario workspace selector does not point at its dedicated branch.' }
        $paths = @(); $totalBytes = 0
        for ($index = 0; $index -lt 65; $index++) {
            $relative = 'file-' + ('{0:D3}' -f $index) + '.txt'; $paths += $relative
            $body = ''
            if ($content -eq 'identical') { $body = "identical small content`n" }
            elseif ($content -eq 'different') { $body = 'different small content ' + ('{0:D3}' -f $index) + "`n" }
            $bytes = $utf8.GetBytes($body); $totalBytes += $bytes.Length
            [IO.File]::WriteAllBytes((Join-Path $workspace $relative), $bytes)
        }
        Invoke-Cm (@('add') + $paths) $workspace -RequireSuccess | Out-Null
        $before = Invoke-Cm @('status', '--xml', '--encoding=utf-8', '--fullpaths') $workspace -RequireSuccess
        $serverBefore = Server-Changesets $workspace $branch
        $comment = 'QA transport ' + $runId + ' ' + $scenarioName
        $arguments = @('checkin') + $paths + @('-c=' + $comment)
        if ($mode -eq 'partial') { $arguments = @('partial', 'checkin') + $paths + @('--all', ('-c=' + $comment)) }
        if ($CheckinClientConfig) { $arguments += @('--clientconf=' + $CheckinClientConfig) }
        Write-Host ('Checkin once: ' + $scenarioName + ' (65 files, ' + $totalBytes + ' content bytes)')
        $checkin = Invoke-Cm $arguments $workspace
        $after = Invoke-Cm @('status', '--xml', '--encoding=utf-8', '--fullpaths') $workspace
        $header = Invoke-Cm @('status', '--header') $workspace
        $serverAfter = Server-Changesets $workspace $branch
        $verification = Get-CheckinVerification $before $after $header $serverBefore $serverAfter $checkin
        $scenario = [ordered]@{
            name = $scenarioName; branch = $branch; workspace = $workspace; workspaceName = $name
            mode = $mode; content = $content; fileCount = 65; totalContentBytes = $totalBytes
            checkin = $checkin; pendingBefore = $verification.pendingBefore; pendingAfter = $verification.pendingAfter
            statusBefore = $before; statusAfter = $after; statusHeader = $header
            serverBefore = $serverBefore; serverAfter = $serverAfter; createdChangesets = $verification.createdChangesets
            verified = $verification.verified; verificationErrors = $verification.errors; atomicSuccess = $verification.atomicSuccess
        }
        $script:record.scenarios += $scenario; Save-Results
        $changesetText = if ($verification.serverVerified) { $verification.createdChangesets -join ',' } else { '<unverified>' }
        Write-Host ('RESULT ' + $scenarioName + ': exit=' + $checkin.exitCode + ', ms=' + $checkin.elapsedMs + ', pending=' + $verification.pendingAfter + ', verified=' + $verification.verified + ', new cs=' + $changesetText)
    }
}
if ([IO.File]::ReadAllText($selectorPath) -cne $referenceSelector) { throw 'Reference selector changed during independent test.' }
$script:record.complete = $true; $script:record.completedAt = (Get-Date).ToString('o'); Save-Results
}
catch { $script:record.error = $_.Exception.ToString(); throw }
finally {
    if ($networkPending) {
        $networkCompleted = $false
        try {
            # The probe has no repository writes. Stop it on exceptional exit
            # before draining and preserving both raw output streams.
            if (-not $script:record.complete -and -not $networkPending.process.HasExited) {
                $networkPending.process.Kill(); $networkPending.process.WaitForExit()
            }
            $script:record.network = Complete-Cm $networkPending
            $networkCompleted = $true
        }
        catch {
            $script:record.network = [ordered]@{
                error = $_.Exception.ToString(); stdoutRaw = $networkPending.stdoutPath; stderrRaw = $networkPending.stderrPath
            }
        }
        finally {
            if (-not $networkCompleted -and -not $networkPending.process.HasExited) { $networkPending.process.Kill(); $networkPending.process.WaitForExit() }
            $networkPending.stdoutFile.Dispose(); $networkPending.stderrFile.Dispose(); $networkPending.process.Dispose()
        }
    }
    Save-Results
}
Write-Output $resultsPath
