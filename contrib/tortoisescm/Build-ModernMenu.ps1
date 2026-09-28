# Builds an unsigned, identity-only preview package. Does not install it.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$MakeAppxPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
$output = Assert-TscmPlainPath $OutputDirectory
if ([string]::IsNullOrWhiteSpace($MakeAppxPath)) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = @(Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object Name -Match '^10\.0\.\d+\.0$' |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($candidate.Count -eq 0) { throw 'Windows SDK x64 makeappx.exe is required to package the optional modern menu. Supply -MakeAppxPath.' }
    $MakeAppxPath = $candidate[0]
}
$makeappx = Assert-TscmPlainPath $MakeAppxPath
[IO.Directory]::CreateDirectory($output) | Out-Null
$packagePath = Join-TscmOwnedPath $output 'TortoiseSCM.ModernMenu.msix'
if (Test-Path -LiteralPath $packagePath) { throw 'Modern menu package already exists; choose a fresh output directory.' }
$stage = Join-TscmOwnedPath $output ('.modern-stage-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    # Referenced executables and assets live in the external installation directory.
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ModernMenu\AppxManifest.xml') -Destination (Join-Path $stage 'AppxManifest.xml')
    $log = & $makeappx pack /d $stage /p $packagePath /nv 2>&1
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed: $log" }
    Write-Output $packagePath
} finally {
    $checked = Assert-TscmPlainPath $stage
    if (-not $checked.StartsWith($output.TrimEnd('\') + '\.modern-stage-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe sparse-package cleanup path.' }
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}
