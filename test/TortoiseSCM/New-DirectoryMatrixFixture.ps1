# GPL-2.0-or-later. Creates isolated native directory conflict fixtures.
param([string]$ManifestPath = '')
$ErrorActionPreference = 'Stop'
if (!$ManifestPath) { $ManifestPath = & "$PSScriptRoot\New-TestWorkspace.ps1" }
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$m = & "$PSScriptRoot\Read-DirectoryTestManifest.ps1" -ManifestPath $ManifestPath
if (@(Get-ChildItem -Force -LiteralPath $m.producer | Where-Object Name -ne '.plastic').Count -ne 0) { throw 'Directory matrix fixture requires an empty producer workspace' }
$cm = 'D:\Program Files\PlasticSCM5\client\cm.exe'
$utf8 = New-Object Text.UTF8Encoding($false)
function Cm([string[]]$Arguments) {
    $output = & $cm @Arguments
    if ($LASTEXITCODE -ne 0) { throw "cm failed: $Arguments" }
    return ($output -join "`n")
}
function Tree([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Force -Path (Join-Path $m.producer "$Path/nested") | Out-Null
    [IO.File]::WriteAllText((Join-Path $m.producer "$Path/nested/child.txt"), $Text, $utf8)
}
Push-Location -LiteralPath $m.producer
try {
    foreach ($name in @('md','dm','am-old','ma-old','twins-a','twins-b','plain-move','plain-delete')) { Tree $name $name }
    Cm @('add','.', '-R') | Out-Null
    Cm @('checkin','.', '--all', '-c=directory matrix baseline') | Out-Null
    $baseline = [long]((Cm @('status', '--header', '--machinereadable')).Split(' ')[1])
    $sourceBranch = "$($m.branch)/source"
    Cm @('branch','create',"br:$sourceBranch@$($m.repository)", "--changeset=cs:$baseline@$($m.repository)") | Out-Null
    Cm @('switch',"br:$sourceBranch@$($m.repository)") | Out-Null
    Cm @('move','md','md-source') | Out-Null
    Cm @('remove','dm') | Out-Null
    Tree 'am-target' 'source add'
    Cm @('add','am-target','-R') | Out-Null
    Cm @('move','ma-old','ma-target') | Out-Null
    Cm @('move','twins-a','twins-target') | Out-Null
    Cm @('move','plain-move','plain-moved') | Out-Null
    Cm @('remove','plain-delete') | Out-Null
    Cm @('checkin','.', '--all', '-c=directory matrix source') | Out-Null
    $source = [long]((Cm @('status', '--header', '--machinereadable')).Split(' ')[1])
    Cm @('switch',"br:$($m.branch)@$($m.repository)") | Out-Null
    Cm @('remove','md') | Out-Null
    Cm @('move','dm','dm-destination') | Out-Null
    Cm @('move','am-old','am-target') | Out-Null
    Tree 'ma-target' 'destination add'
    Cm @('add','ma-target','-R') | Out-Null
    Cm @('move','twins-b','twins-target') | Out-Null
    Cm @('checkin','.', '--all', '-c=directory matrix destination') | Out-Null
    $destination = [long]((Cm @('status', '--header', '--machinereadable')).Split(' ')[1])
    $preview = Cm @('merge', "cs:$source", '--printcontributors','--machinereadable','--fieldseparator=|','--nointeractiveresolution')
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'directory-matrix-preview.txt'), $preview, $utf8)
    $matrix = [ordered]@{ manifest = $ManifestPath; baseline = $baseline; source = $source; destination = $destination; sourceBranch = $sourceBranch }
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'directory-matrix.json'), ($matrix | ConvertTo-Json), $utf8)
    Write-Output $preview
    Write-Output (Join-Path $m.runDirectory 'directory-matrix.json')
} finally { Pop-Location }
