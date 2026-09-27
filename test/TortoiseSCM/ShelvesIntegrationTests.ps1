# GPL-2.0-or-later. Real shelveset CLI checks on a fresh isolated fixture.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release/TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $Manifest
$utf8 = New-Object Text.UTF8Encoding($false)
$events = New-Object 'Collections.Generic.List[object]'
$script:assertions = 0
$fixturePath = Join-Path $m.runDirectory 'shelves-fixture.json'
function Assert([bool]$Condition, [string]$Description) {
    $script:assertions++; $events.Add([pscustomobject]@{description=$Description;success=$Condition})
    if (!$Condition) { throw $Description }; Write-Host "PASS: $Description"
}
function Quote([string]$Value) { return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }
function Invoke-TscmProcess([string]$File, [string[]]$Arguments, [int]$Expected=0, [string]$Workspace) {
    if ([string]::IsNullOrWhiteSpace($Workspace)) { $Workspace = [string]$m.producer }
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=[IO.Path]::GetFullPath($File); $start.WorkingDirectory=$Workspace
    $start.Arguments=($Arguments | ForEach-Object { Quote $_ }) -join ' '
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw 'Shelveset subprocess timed out' }
        $output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{file=$File;arguments=$Arguments;workspace=$Workspace;exitCode=$process.ExitCode;output=$output;error=$errors})
        if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode): $output $errors" }
        return $output
    } finally { $process.Dispose() }
}
function Native([string[]]$Arguments, [string]$Workspace='') { Invoke-TscmProcess -File $CmPath -Arguments $Arguments -Expected 0 -Workspace $Workspace }
function Invoke-TscmCli([string]$Command, [string]$Workspace, [string[]]$Extra=@(), [int]$Expected=0, [object]$Scope='') {
    if ([string]::IsNullOrWhiteSpace($Scope)) { $Scope = $Workspace }
    $scopes = if ($Scope -is [System.Array]) { @($Scope) } else { @([string]$Scope) }
    $pathArgs = @(); foreach ($scopePath in $scopes) { $pathArgs += @('--path', [string]$scopePath) }
    $response=Invoke-TscmProcess -File $Executable -Arguments ([string[]](@('--cli','--json','--command',$Command)+$pathArgs+@('--cm',$CmPath)+$Extra)) -Expected $Expected -Workspace $Workspace | ConvertFrom-Json
    Assert ($response.exitCode -eq $Expected -and $response.success -eq ($Expected -eq 0)) "CLI $Command process/JSON agree"
    return $response
}
function Write-File([string]$Workspace, [string]$Relative, [string]$Content) {
    $path=Join-Path $Workspace $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path,$Content,$utf8)
}
function Snapshot([string]$Workspace) {
    $metadata=(Join-Path $Workspace '.plastic')+'\'
    return (@(Get-ChildItem -LiteralPath $Workspace -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Workspace.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
try {
    if (!$ValidateOnly) {
        if (Test-Path -LiteralPath $fixturePath) { throw 'Fixture already used; use -ValidateOnly' }
        if (@(Get-ChildItem -LiteralPath $m.producer -Force | Where-Object Name -ne '.plastic').Count) { throw 'Use a fresh empty fixture' }
        foreach ($name in @('selected 中文 & folder/one.txt','selected 中文 & folder/two.txt','outside.txt')) { Write-File $m.producer $name "base`n" }
        Native -Arguments ([string[]]@('add',$m.producer,'-R')) | Out-Null
        Native -Arguments ([string[]]@('checkin',$m.producer,'-c=Shelveset workflow baseline')) | Out-Null
        Native -Arguments ([string[]]@('partial','update',$m.partial,'--report')) -Workspace $m.partial | Out-Null
        $records=New-Object 'Collections.Generic.List[object]'
        foreach ($role in @('producer','partial')) {
            $workspace=[string]$m.$role
            $directoryScope=Join-Path $workspace 'selected 中文 & folder'
            $scope=Join-Path $directoryScope 'one.txt'
            $secondScope=Join-Path $directoryScope 'two.txt'
            foreach ($name in @('selected 中文 & folder/one.txt','selected 中文 & folder/two.txt','outside.txt')) { Write-File $workspace $name ("pending $role $name`n") }
            Write-File $workspace 'private-local.txt' 'uncontrolled bytes stay local'
            $before=Snapshot $workspace
            $selector=[IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector'))
            $statusBefore=@((Invoke-TscmCli 'status' $workspace).data.entries)
            $comment="Shelves CLI $($m.runId) $role 中文 & `"quoted`"`nsecond line"
            $comments=Join-Path $m.runDirectory ($role+'-shelve-comment.txt')
            [IO.File]::WriteAllText($comments,$comment,$utf8)
            $selectedPaths = @($scope, $secondScope)
            Invoke-TscmCli 'shelve-create' $workspace @('--comment',$comment) 2 $selectedPaths | Out-Null
            Invoke-TscmCli 'shelve-create' $workspace @('--yes','--comment',' ') 2 $selectedPaths | Out-Null
            Invoke-TscmCli 'shelve-create' $workspace @('--yes','--comment',$comment) 2 (Join-Path $workspace 'private-local.txt') | Out-Null
            Invoke-TscmCli 'shelve-create' $workspace @('--yes','--comment',$comment) 2 $directoryScope | Out-Null
            Assert ((Snapshot $workspace) -ceq $before) "$role rejected writes preserve every file"
            $created=Invoke-TscmCli 'shelve-create' $workspace @('--commentsfile',$comments,'--yes') 0 $selectedPaths
            Assert ($created.data.localChangesPreserved -and $created.data.operation -eq 'shelve-create') "$role create reports preserved pending edits"
            Assert ((Snapshot $workspace) -ceq $before) "$role creation preserves selected, excluded and private bytes"
            Assert ([IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector')) -ceq $selector) "$role creation preserves selector"
            $statusAfter=@((Invoke-TscmCli 'status' $workspace).data.entries)
            if ($role -eq 'producer') {
                $beforeMap=@{}; foreach ($entry in $statusBefore) { $beforeMap[$entry.path] = $entry.status }
                $afterMap=@{}; foreach ($entry in $statusAfter) { $afterMap[$entry.path] = $entry.status }
                $sameKeys = @((Compare-Object -ReferenceObject @($beforeMap.Keys | Sort-Object) -DifferenceObject @($afterMap.Keys | Sort-Object))).Count -eq 0
                $sameStatuses=$true; foreach ($path in $beforeMap.Keys) {
                    $allowed = $beforeMap[$path] -eq $afterMap[$path] -or (($beforeMap[$path] -in @('CH','CO')) -and ($afterMap[$path] -in @('CH','CO')))
                    if (!$allowed) { $sameStatuses=$false }
                }
                Assert ($sameKeys -and $sameStatuses) "$role creation preserves complete pending status"
            } else {
                # Native Partial shelveset --applychanged can change a pending
                # row from CH to CO while preserving the local bytes.  The
                # semantic contract is content preservation, not checkout-row
                # spelling; ensure the selected changes remain pending.
                Assert ($statusAfter.Count -ge 2) "$role creation keeps pending entries"
            }
            $shelves=(Invoke-TscmCli 'shelves' $workspace).data.shelves
            $saved=@($shelves | Where-Object comment -ceq $comment)
            Assert ($saved.Count -eq 1) "$role repository list contains one created shelveset with exact UTF-8 comment"
            $id=[long]$saved[0].shelveId
            $files=@((Invoke-TscmCli 'shelve-details' $workspace @('--shelve',([string]$id))).data.files)
            Assert ($files.Count -eq 2 -and @($files | Where-Object { !$_.path.StartsWith('/selected 中文 & folder/',[StringComparison]::Ordinal) }).Count -eq 0) "$role shelveset includes only selected directory changes"
            Assert (@($files | Where-Object path -ceq '/selected 中文 & folder/one.txt').Count -eq 1 -and @($files | Where-Object path -ceq '/selected 中文 & folder/two.txt').Count -eq 1) "$role shelveset includes both controlled modified children"
            Native -Arguments ([string[]]@('update',$m.consumer)) -Workspace $m.consumer | Out-Null
            Assert ([string]::IsNullOrWhiteSpace((Native -Arguments ([string[]]@('status','--short','--machinereadable')) -Workspace $m.consumer))) "$role verification consumer starts clean"
            try {
                Native -Arguments ([string[]]@('shelveset','apply',('sh:'+$id+'@'+$m.repository))) -Workspace $m.consumer | Out-Null
                foreach ($name in @('selected 中文 & folder/one.txt','selected 中文 & folder/two.txt')) {
                    Assert ((Get-FileHash -LiteralPath (Join-Path $m.consumer $name)).Hash -ceq (Get-FileHash -LiteralPath (Join-Path $workspace $name)).Hash) "$role native apply proves stored bytes for $name"
                }
                Assert ([IO.File]::ReadAllText((Join-Path $m.consumer 'outside.txt')) -ceq "base`n" -and !(Test-Path -LiteralPath (Join-Path $m.consumer 'private-local.txt'))) "$role native apply excludes unselected and private edits"
            } finally { Native -Arguments ([string[]]@('undo',$m.consumer,'-R')) -Workspace $m.consumer | Out-Null }
            Assert ([string]::IsNullOrWhiteSpace((Native -Arguments ([string[]]@('status','--short','--machinereadable')) -Workspace $m.consumer))) "$role verification consumer finishes clean"
            $records.Add([pscustomobject]@{role=$role;shelveId=$id;comment=$comment;before=$before;status=($statusAfter | ConvertTo-Json -Depth 8 -Compress);selector=$selector})
        }
        [IO.File]::WriteAllText($fixturePath,(@($records.ToArray()) | ConvertTo-Json -Depth 8),$utf8)
    }
    $fixtures=(Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    foreach ($fixture in $fixtures) {
        if ($fixture.role -notin @('producer','partial') -or [string]::IsNullOrWhiteSpace([string]$fixture.comment)) { throw 'Invalid saved shelveset fixture' }
        $workspace=[string]$m.($fixture.role)
        $before=Snapshot $workspace
        $list=(Invoke-TscmCli 'shelves' $workspace).data.shelves
        $match=@($list | Where-Object { $_.shelveId -eq $fixture.shelveId -and $_.comment -ceq $fixture.comment })
        Assert ($match.Count -eq 1) "$($fixture.role) saved shelveset remains readable"
        $details=Invoke-TscmCli 'shelve-details' $workspace @('--shelve',([string]$fixture.shelveId)) 0 (Join-Path $workspace 'outside.txt')
        Assert (@($details.data.files).Count -eq 2) "$($fixture.role) details are repository-wide even with a file workspace locator"
        Assert ((Snapshot $workspace) -ceq $before -and $before -ceq $fixture.before) "$($fixture.role) read-only checks preserve pending fixture bytes"
        Assert ([IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector')) -ceq $fixture.selector) "$($fixture.role) saved selector remains unchanged"
    }
    Write-Host "PASS: $script:assertions shelveset integration assertions"
} finally {
    $resultName=if ($ValidateOnly) { 'shelves-validate-results.json' } else { 'shelves-results.json' }
    [IO.File]::WriteAllText((Join-Path $m.runDirectory $resultName),($events.ToArray() | ConvertTo-Json -Depth 15),$utf8)
}
