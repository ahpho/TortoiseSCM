# GPL-2.0-or-later. Disposable repository labels on validated isolated branch snapshots.
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release/TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe',
    [string]$UiExecutable
)
$ErrorActionPreference = 'Stop'
$m = & (Join-Path $PSScriptRoot 'Read-DirectoryTestManifest.ps1') -ManifestPath $Manifest
$utf8 = New-Object Text.UTF8Encoding($false)
$events = New-Object 'Collections.Generic.List[object]'
$assertions = 0
function Assert([bool]$condition, [string]$message) {
    $script:assertions++; $events.Add([pscustomobject]@{check=$message;passed=$condition})
    if (!$condition) { throw $message }; Write-Host "PASS: $message"
}
function Quote([string]$value) { '"' + [regex]::Replace([regex]::Replace($value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }
function Run([string]$file, [string]$workspace, [string[]]$arguments, [int]$expected=0) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=[IO.Path]::GetFullPath($file); $start.WorkingDirectory=$workspace
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=$utf8; $start.StandardErrorEncoding=$utf8
    $start.Arguments=($arguments | ForEach-Object { Quote $_ }) -join ' '
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'Subprocess timed out; inspect the isolated fixture before retrying.' }
        $text=$stdout.GetAwaiter().GetResult(); $errorText=$stderr.GetAwaiter().GetResult()
        $events.Add([pscustomobject]@{command=$arguments;workspace=$workspace;exit=$process.ExitCode;output=$text;error=$errorText})
        if ($process.ExitCode -ne $expected) { throw "Exit $($process.ExitCode), expected $expected`: $text $errorText" }
        return $text
    } finally { $process.Dispose() }
}

function Invoke-LabelCli([string]$command, [string]$workspace, [string[]]$extra=@(), [int]$expected=0) {
    Run $Executable $workspace (@('--cli','--json','--cm',$CmPath,'--command',$command,'--path',$workspace)+$extra) $expected | ConvertFrom-Json
}
function StableStatus([string]$workspace) {
    # cm status does not guarantee row order, especially for Partial workspaces.
    (([string](Run $CmPath $workspace @('status','--short','--machinereadable')) -split '\r?\n' | Where-Object { $_.Length -gt 0 } | Sort-Object) -join "`n")
}
function Snapshot([string]$workspace) {
    $metadata=(Join-Path $workspace '.plastic')+'\'
    (@(Get-ChildItem -LiteralPath $workspace -Recurse -File -Force | Where-Object { !$_.FullName.StartsWith($metadata,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($workspace.Length)+'|'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
function NativeLabels {
    $xml=[xml](Run $CmPath $m.consumer @('find','label',("on repository '"+$m.repository+"'"),'--xml','--encoding=utf-8'))
    @($xml.PLASTICQUERY.MARKER | Where-Object { $_ })
}
function LabelInventory {
    (@(NativeLabels | Sort-Object ID | ForEach-Object { $_.OuterXml }) -join "`n")
}
$output=Join-Path $m.runDirectory ('labels-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($output) | Out-Null
$before=@{}
$owned=New-Object 'Collections.Generic.List[object]'
$baseline=LabelInventory
try {
    foreach ($workspace in @($m.producer,$m.consumer,$m.partial)) {
        $before[$workspace]=@{
            status=(StableStatus $workspace)
            selector=[IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector'))
            tree=(Get-FileHash -LiteralPath (Join-Path $workspace '.plastic/plastic.wktree')).Hash
            files=(Snapshot $workspace)
        }
    }
    $cs=[string](Invoke-LabelCli 'branch-head' $m.consumer @('--branch',$m.branch)).data.changeset
    Assert ([long]$cs -gt 0) 'Fixture has a committed isolated snapshot'
    $log=[xml](Run $CmPath $m.consumer @('log',("cs:"+$cs+'@'+$m.repository),'--xml','--encoding=utf-8'))
    Assert (@($log.LogList.Changeset).Count -eq 1 -and [string]$log.LogList.Changeset.ChangesetId -ceq $cs -and [string]$log.LogList.Changeset.Branch -ceq $m.branch) 'Native changeset belongs to exact isolated branch'
    foreach ($creator in @($m.producer,$m.partial)) {
        $name='tortoisescm-autotest-label-中文 & '+[Guid]::NewGuid().ToString('N')
        $comment="Label integration Unicode 注释 & review`r`nSecond line 第二行"
        Invoke-LabelCli 'label-create' $creator @('--label',$name,'--changeset',$cs,'--comment',$comment) 2 | Out-Null
        Assert (@(NativeLabels | Where-Object NAME -ceq $name).Count -eq 0) 'Missing consent does not create a label'
        Invoke-LabelCli 'label-create' $creator @('--label',$name,'--changeset',$cs,'--comment',$comment,'--yes') | Out-Null
        $native=@(NativeLabels | Where-Object NAME -ceq $name)
        Assert ($native.Count -eq 1 -and [long]$native[0].CHANGESET -eq [long]$cs) 'Native label points to requested snapshot'
        $identity=[string]$native[0].ID
        $owned.Add([pscustomobject]@{name=$name;id=$identity;changeset=$cs})
        if ($UiExecutable) {
            Run $UiExecutable $creator @('--labels-live',(Join-Path $output (Split-Path $creator -Leaf)),$creator,$name) | Out-Null
            Assert $true 'Live native WinForms label selection, changeset files and snapshot navigation pass'
        }
        foreach ($workspace in @($m.producer,$m.consumer,$m.partial)) {
            $resolved=(Invoke-LabelCli 'label-resolve' $workspace @('--label',$name)).data.label
            Assert ($resolved.name -ceq $name -and [string]$resolved.id -ceq $identity -and [string]$resolved.changeset -ceq $cs -and $resolved.repository -ceq $m.repository -and $resolved.comment.Replace("`r`n","`n") -ceq $comment.Replace("`r`n","`n")) 'Resolve matches native identity, Unicode, multiline comment, repository and snapshot'
            $listed=@((Invoke-LabelCli 'labels' $workspace @('--filter',$name)).data.labels)
            Assert ($listed.Count -eq 1 -and [string]$listed[0].id -ceq $identity) 'Filtered listing returns exact created label'
            $listing=Invoke-LabelCli 'repository-list' $workspace @('--changeset',([string]$resolved.changeset))
            Assert ($listing.data.changeset -eq $resolved.changeset -and @($listing.data.entries).Count -gt 0) 'Resolved label opens fixed populated snapshot in Standard and Partial'
        }
        Invoke-LabelCli 'label-create' $creator @('--label',$name,'--changeset',$cs,'--comment','duplicate','--yes') 2 | Out-Null
        Invoke-LabelCli 'label-delete' $creator @('--label',$name,'--label-id',([string]([long]$identity+1)),'--changeset',$cs,'--yes') 1 | Out-Null
        Invoke-LabelCli 'label-delete' $creator @('--label',$name,'--label-id',$identity,'--changeset',([string]([long]$cs+1)),'--yes') 1 | Out-Null
        $unchanged=@(NativeLabels | Where-Object NAME -ceq $name)
        Assert ($unchanged.Count -eq 1 -and [string]$unchanged[0].ID -ceq $identity -and [string]$unchanged[0].CHANGESET -ceq $cs -and ([string]$unchanged[0].COMMENT).Replace("`r`n","`n") -ceq $comment.Replace("`r`n","`n")) 'Duplicate create and stale delete preserve server label'
        Invoke-LabelCli 'label-delete' $creator @('--label',$name,'--label-id',$identity,'--changeset',$cs,'--yes') | Out-Null
        Assert (@(NativeLabels | Where-Object NAME -ceq $name).Count -eq 0) 'Reviewed delete removes only disposable label'
        Invoke-LabelCli 'label-resolve' $creator @('--label',$name) 2 | Out-Null
    }
} finally {
    try {
        foreach ($label in $owned) {
            $remaining=@(NativeLabels | Where-Object NAME -ceq $label.name)
            if ($remaining.Count -eq 1 -and [string]$remaining[0].ID -ceq $label.id -and [string]$remaining[0].CHANGESET -ceq $label.changeset) {
                Invoke-LabelCli 'label-delete' $m.consumer @('--label',$label.name,'--label-id',$label.id,'--changeset',$label.changeset,'--yes') | Out-Null
            } elseif ($remaining.Count -gt 0) { throw 'Disposable label changed externally; refusing cleanup. Inspect results.json.' }
        }
        Assert ((LabelInventory) -ceq $baseline) 'Complete repository label inventory restored; no temporary label left'
        foreach ($workspace in $before.Keys) {
            Assert ([string](StableStatus $workspace) -ceq [string]$before[$workspace].status) 'Labels preserve pending status'
            Assert ([IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector')) -ceq $before[$workspace].selector) 'Labels preserve workspace selector'
            Assert ((Get-FileHash -LiteralPath (Join-Path $workspace '.plastic/plastic.wktree')).Hash -ceq $before[$workspace].tree) 'Labels preserve loaded workspace tree'
            Assert ((Snapshot $workspace) -ceq $before[$workspace].files) 'Labels preserve every existing working file byte'
        }
    } finally {
        [IO.File]::WriteAllText((Join-Path $output 'results.json'),($events.ToArray() | ConvertTo-Json -Depth 20),$utf8)
    }
}
Write-Host "PASS: $assertions label integration assertions; $output"
