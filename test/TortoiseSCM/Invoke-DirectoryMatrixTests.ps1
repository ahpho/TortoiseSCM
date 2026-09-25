# GPL-2.0-or-later. Native server-backed src/dst and conflict-free directory tests.
param([string]$MatrixPath = '', [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$cm = 'D:\Program Files\PlasticSCM5\client\cm.exe'
$utf8 = New-Object Text.UTF8Encoding($false)
if (!$MatrixPath) { $MatrixPath = & "$PSScriptRoot\New-DirectoryMatrixFixture.ps1" | Select-Object -Last 1 }
$matrix = Get-Content -Raw -Encoding UTF8 -LiteralPath $MatrixPath | ConvertFrom-Json
$m = & "$PSScriptRoot\Read-DirectoryTestManifest.ps1" -ManifestPath $matrix.manifest
if (![String]::Equals([IO.Path]::GetFullPath($MatrixPath), (Join-Path $m.runDirectory 'directory-matrix.json'), [StringComparison]::OrdinalIgnoreCase) -or
    $matrix.sourceBranch -cne ($m.branch + '/source') -or $matrix.baseline -le 0 -or $matrix.source -le $matrix.baseline -or $matrix.destination -le $matrix.source) { throw 'Matrix metadata is outside the isolated fixture or has invalid contributors' }
$exe = Join-Path $sourceRoot 'bin/TortoiseSCM/qa/DirectoryMatrixTests.exe'
function Cm([string[]]$Arguments) {
    $output = & $cm @Arguments
    if ($LASTEXITCODE -ne 0) { throw "cm failed: $Arguments" }
    return ($output -join "`n")
}
function Test([string[]]$Arguments) {
    $output = & $exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Directory matrix test failed: $Arguments" }
    return ($output -join "`n")
}
Push-Location -LiteralPath $sourceRoot
try {
    $sources = @(Get-ChildItem src/TortoiseSCM/Core -Filter '*.cs' | ForEach-Object FullName)
    $sources += (Join-Path $PSScriptRoot 'DirectoryMatrixTests.cs')
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /langversion:5 /warnaserror /target:exe /platform:x64 /r:System.Xml.Linq.dll "/out:$exe" $sources
    if ($LASTEXITCODE -ne 0) { throw 'Directory matrix compilation failed' }
    if (!$VerifyOnly) {
        Cm @('branch','create',"br:$($m.branch)/destination-choice@$($m.repository)", "--changeset=cs:$($matrix.destination)@$($m.repository)") | Out-Null
        Cm @('switch',"br:$($m.branch)/destination-choice@$($m.repository)", "--workspace=$($m.consumer)") | Out-Null
        Cm @('branch','create',"br:$($m.branch)/no-conflicts@$($m.repository)", "--changeset=cs:$($matrix.baseline)@$($m.repository)") | Out-Null
        $plain = Join-Path $m.runDirectory 'plain'
        New-Item -ItemType Directory -Path $plain | Out-Null
        Cm @('workspace','create',"tscm-$($m.runId)-plain", $plain, $m.repository) | Out-Null
        Cm @('switch',"br:$($m.branch)/no-conflicts@$($m.repository)", "--workspace=$plain") | Out-Null
        Push-Location -LiteralPath $plain
        try {
            [IO.File]::WriteAllText((Join-Path $plain 'ignore.conf'), '*.tscm-ignored', $utf8)
            Cm @('add','ignore.conf') | Out-Null
            Cm @('checkin','ignore.conf','-c=directory ignored descendant fixture') | Out-Null
        } finally { Pop-Location }
        Test @('--matrix', $m.producer, "$($matrix.source)", 'src')
        Test @('--matrix', $m.consumer, "$($matrix.source)", 'dst')
        Test @('--plain', $plain, "$($matrix.source)", 'plain')
    }
    $verifier = Join-Path $m.runDirectory 'verifier'
    if (!(Test-Path -LiteralPath $verifier)) {
        New-Item -ItemType Directory -Path $verifier | Out-Null
        Cm @('workspace','create',"tscm-$($m.runId)-verifier", $verifier, $m.repository) | Out-Null
    }
    $results = @()
    foreach ($case in @(@{branch=$m.branch; choice='src'}, @{branch="$($m.branch)/destination-choice"; choice='dst'}, @{branch="$($m.branch)/no-conflicts"; choice='src'})) {
        Cm @('switch',"br:$($case.branch)@$($m.repository)", "--workspace=$verifier") | Out-Null
        $output = Test @('--verify', $verifier, $case.choice)
        Push-Location -LiteralPath $verifier
        try {
            $changeset = [long]((Cm @('status','--header','--machinereadable')).Split(' ')[1])
            $links = Cm @('find','merge',"where dstchangeset = $changeset",'--xml')
            $file = Join-Path $m.runDirectory "directory-matrix-links-$changeset.xml"
            [IO.File]::WriteAllText($file, $links, $utf8)
            $document = [xml]$links
            if (@($document.PLASTICQUERY.MERGE | Where-Object { $_.SRCCHANGESET -eq "$($matrix.source)" -and $_.DSTCHANGESET -eq "$changeset" }).Count -eq 0) { throw 'Missing native merge link' }
            $results += [ordered]@{ branch=$case.branch; choice=$case.choice; changeset=$changeset; verified=$true; result=$output; nativeMergeLink=$file }
        } finally { Pop-Location }
    }
    [IO.File]::WriteAllText((Join-Path $m.runDirectory 'directory-matrix-results.json'), ($results | ConvertTo-Json -Depth 5), $utf8)
    Write-Output ($results | ConvertTo-Json -Depth 5)
} finally { Pop-Location }
