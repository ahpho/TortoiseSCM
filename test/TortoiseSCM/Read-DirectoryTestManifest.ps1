# GPL-2.0-or-later. Validate every externally supplied fixture before native writes.
param([Parameter(Mandatory=$true)][string]$ManifestPath)
$ErrorActionPreference = 'Stop'
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
$qa = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../bin/TortoiseSCM/qa')).TrimEnd('\')
$m = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestFile | ConvertFrom-Json
if (!$m.complete -or $m.runId -cnotmatch '^integration-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$') { throw 'Only completed isolated integration fixtures are accepted' }
$expectedRoot = Join-Path $qa $m.runId
if (![String]::Equals([IO.Path]::GetFullPath($m.runDirectory), $expectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    ![String]::Equals($manifestFile, (Join-Path $expectedRoot 'manifest.json'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture manifest must be under this checkout qa/integration-* directory' }
if ($m.branch -cne ('/main/tortoisescm-autotest-' + $m.runId) -or $m.repository -cnotmatch '^TestSCM@[^\s"\r\n]+$') { throw 'Fixture repository or branch is not the authorized isolated TestSCM branch' }
foreach ($role in @('producer','consumer','partial','plain','verifier')) {
    $workspace = Join-Path $expectedRoot $role
    if ($role -in @('producer','consumer','partial') -and ![String]::Equals([IO.Path]::GetFullPath($m.$role), $workspace, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe fixture workspace: $role" }
    $cursor = $workspace
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -Force -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture paths cannot contain junctions or symlinks' }
        }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $workspace) {
        $selectorPath = Join-Path $workspace '.plastic/plastic.selector'
        if (!(Test-Path -LiteralPath $selectorPath)) { throw "Fixture workspace has no selector: $role" }
        foreach ($path in @((Join-Path $workspace '.plastic'), $selectorPath)) {
            if ((Get-Item -Force -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture metadata cannot be redirected' }
        }
        $selector = [IO.File]::ReadAllText($selectorPath)
        if ([regex]::Matches($selector, '(?m)^\s*repository\s+"' + [regex]::Escape($m.repository) + '"\s*$').Count -ne 1 -or
            [regex]::Matches($selector, '(?m)^\s*(?:smartbranch|branch)\s+"' + [regex]::Escape($m.branch) + '(?:/[^"\r\n]+)?"\s*$').Count -ne 1 -or
            [regex]::Matches($selector, '(?m)^\s*repository\s+').Count -ne 1) { throw "Fixture selector does not select its isolated branch: $role" }
    } elseif ($role -in @('producer','consumer','partial')) { throw "Missing fixture workspace: $role" }
}
return $m
