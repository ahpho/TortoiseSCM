# Only test-owned GUI processes may be closed by this suite.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $repo ('bin/TortoiseSCM/qa/setup-applications-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$hostExe = Join-Path $fixture 'SetupLockHost.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:winexe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$hostExe" (Join-Path $PSScriptRoot 'SetupLockHost.cs')
if ($LASTEXITCODE -ne 0) { throw 'Lock host compilation failed.' }
$dll = Join-Path $fixture 'TortoiseSCMShell.dll'
Copy-Item -LiteralPath (Join-Path $repo 'bin/TortoiseSCM/Release/TortoiseSCMShell.dll') -Destination $dll
Add-Type -Path (Join-Path $repo 'contrib/tortoisescm/SetupApplications.cs')
$children = New-Object 'Collections.Generic.List[Diagnostics.Process]'
function Start-LockHost([string]$mode) {
    $marker = Join-Path $fixture ([Guid]::NewGuid().ToString('N') + '.txt')
    $process = Start-Process -FilePath $hostExe -ArgumentList ('"' + $dll + '" "' + $marker + '" ' + $mode) -WindowStyle Hidden -PassThru
    $children.Add($process)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $marker)) {
        if ($process.HasExited -or $timer.Elapsed.TotalSeconds -gt 20) { throw 'Lock host did not become ready.' }
        Start-Sleep -Milliseconds 100
    }
    return $process
}
function Assert([bool]$value, [string]$message) { if (-not $value) { throw $message }; Write-Host "PASS: $message" }
try {
    $first = Start-LockHost 'close'
    $approved = @([TortoiseSCM.Setup.Applications]::Find(@($dll)) | Where-Object Id -eq $first.Id)
    Assert ($approved.Count -eq 1 -and $approved[0].CanClose) 'Restart Manager identifies the test GUI holding the DLL'
    $second = Start-LockHost 'close'
    [TortoiseSCM.Setup.Applications]::Close($approved, @($dll))
    Assert ($first.WaitForExit(10000)) 'Confirmed application exits normally through Restart Manager'
    Assert (-not $second.HasExited) 'A new application started after confirmation is not closed'
    $approvedSecond = @([TortoiseSCM.Setup.Applications]::Find(@($dll)) | Where-Object Id -eq $second.Id)
    [TortoiseSCM.Setup.Applications]::Close($approvedSecond, @($dll))
    Assert ($second.WaitForExit(10000)) 'Unapproved test process can be cleaned up normally'
    $veto = Start-LockHost 'veto'
    $approved = @([TortoiseSCM.Setup.Applications]::Find(@($dll)) | Where-Object Id -eq $veto.Id)
    $rejected = $false
    try { [TortoiseSCM.Setup.Applications]::Close($approved, @($dll)) } catch { $rejected = $true }
    Assert ($rejected -and -not $veto.HasExited) 'Application veto preserves the process instead of forcing termination'
    $changed = New-Object TortoiseSCM.Setup.BlockingApplication
    $changed.Id = $veto.Id; $changed.StartTime = $approved[0].StartTime - 1
    [TortoiseSCM.Setup.Applications]::Close(@($changed), @($dll))
    Assert (-not $veto.HasExited) 'Mismatched process creation time cannot authorize closing a recycled PID'
    Write-Host ('PASS: application shutdown tests; artifacts: ' + $fixture)
} finally {
    foreach ($child in $children) {
        # These retained process objects were created by this test alone. The veto
        # fixture intentionally cannot accept normal shutdown, so clean it up here.
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
}
