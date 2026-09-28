# GPL-2.0-or-later. Read-only server tests; exports go outside all workspaces.
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [string]$Executable = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release/TortoiseSCM.exe'),
    [string]$CmPath = 'D:\Program Files\PlasticSCM5\client\cm.exe'
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

function Invoke-BrowserCli([string]$command, [string]$workspace, [string[]]$extra=@(), [int]$expected=0) {
    Run $Executable $workspace (@('--cli','--json','--cm',$CmPath,'--command',$command,'--path',$workspace)+$extra) $expected | ConvertFrom-Json
}
function StableStatus([string]$workspace) {
    # cm status does not guarantee row order, especially for Partial workspaces.
    (([string](Run $CmPath $workspace @('status','--short','--machinereadable')) -split '\r?\n' | Where-Object { $_.Length -gt 0 } | Sort-Object) -join "`n")
}
$output=Join-Path $m.runDirectory ('repository-browser-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($output) | Out-Null
$before=@{}
try {
    foreach ($workspace in @($m.producer,$m.consumer,$m.partial)) {
        $before[$workspace]=@{
            status=(StableStatus $workspace)
            selector=[IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector'))
            tree=(Get-FileHash -LiteralPath (Join-Path $workspace '.plastic/plastic.wktree')).Hash
        }
    }
    $cs=[string](Invoke-BrowserCli 'branch-head' $m.consumer @('--branch',$m.branch)).data.changeset
    Assert ([long]$cs -gt 0) 'Fixture has a committed snapshot'
    $directory='/selected 中文 & folder'
    $roots=@()
    foreach ($workspace in @($m.producer,$m.consumer,$m.partial)) {
        $root=Invoke-BrowserCli 'repository-list' $workspace @('--changeset',$cs)
        Assert ($root.data.changeset -eq [long]$cs -and $root.data.repository -ceq $m.repository) 'Listing returns fixed snapshot and repository'
        Assert (@($root.data.entries | Where-Object { $_.path -ceq $directory -and $_.isDirectory }).Count -eq 1) 'Root lists Chinese directory'
        $roots+=($root.data.entries | ConvertTo-Json -Depth 8 -Compress)
        $nested=Invoke-BrowserCli 'repository-list' $workspace @('--changeset',$cs,'--item',$directory)
        Assert (@($nested.data.entries).Count -eq 2 -and @($nested.data.entries | Where-Object isDirectory).Count -eq 0) 'Nested directory lists exactly its two files'
        $native=[xml](Run $CmPath $workspace @('ls',$directory,"--tree=cs:$cs@$($m.repository)",'--xml','--encoding=utf-8','--symlink'))
        foreach ($entry in $nested.data.entries) {
            $item=@($native.LsResults.LsItems.LsItem | Where-Object CurrentPath -ceq $entry.path)
            Assert ($item.Count -eq 1 -and [long]$item[0].ItemId -eq $entry.itemId -and [long]$item[0].Size -eq $entry.size) 'CLI identity and size match native historical XML'
        }
        $empty=Invoke-BrowserCli 'repository-list' $workspace @('--changeset','0')
        Assert (@($empty.data.entries).Count -eq 0) 'Initial empty snapshot returns an empty directory'
        Invoke-BrowserCli 'repository-list' $workspace @('--changeset','0','--item',$directory) 1 | Out-Null
        Invoke-BrowserCli 'repository-list' $workspace @('--changeset',$cs,'--item',($directory+'/one.txt')) 2 | Out-Null
        Invoke-BrowserCli 'repository-list' $workspace @('--changeset',$cs,'--item','/../escape') 2 | Out-Null
        $export=Join-Path $output ((Split-Path $workspace -Leaf)+'.txt')
        Invoke-BrowserCli 'export' $workspace @('--changeset',$cs,'--item',($directory+'/one.txt'),'--output',$export,'--yes') | Out-Null
        $nativeFile=Join-Path $output 'native.txt'
        Run $CmPath $workspace @('cat',("serverpath:"+$directory+"/one.txt#cs:$cs@$($m.repository)"),("--file="+$nativeFile)) | Out-Null
        Assert ((Get-FileHash -LiteralPath $export).Hash -ceq (Get-FileHash -LiteralPath $nativeFile).Hash) 'Export bytes match native selected snapshot'
        Assert ([IO.File]::ReadAllText($export).Contains('base')) 'Historical export ignores pending producer edits'
    }
    Assert ($roots[0] -ceq $roots[1] -and $roots[1] -ceq $roots[2]) 'Standard dirty, Standard clean and Partial return identical snapshots'
} finally {
    try {
    foreach ($workspace in $before.Keys) {
        Assert ([string](StableStatus $workspace) -ceq [string]$before[$workspace].status) 'Browsing preserves pending status'
        Assert ([IO.File]::ReadAllText((Join-Path $workspace '.plastic/plastic.selector')) -ceq $before[$workspace].selector) 'Browsing preserves selector'
        Assert ((Get-FileHash -LiteralPath (Join-Path $workspace '.plastic/plastic.wktree')).Hash -ceq $before[$workspace].tree) 'Browsing preserves loaded workspace tree'
    }
    } finally {
    [IO.File]::WriteAllText((Join-Path $output 'results.json'),($events.ToArray() | ConvertTo-Json -Depth 20),$utf8)
    }
}
Write-Host "PASS: $assertions repository browser integration assertions; $output"
