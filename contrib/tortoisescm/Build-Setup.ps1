[CmdletBinding()]
param(
    [string]$PackageArchive,
    [string]$BeyondCompareDirectory,
    [string]$TortoiseToolsDirectory,
    [string]$BinaryDirectory,
    [string]$OutputDirectory,
    [string]$Version = ('0.1.0-dev-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$IsccPath,
    [switch]$TestSetup
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell -File can evaluate parameter defaults before PSScriptRoot is set.
if ([string]::IsNullOrWhiteSpace($BinaryDirectory)) { $BinaryDirectory = Join-Path $PSScriptRoot '../../bin/TortoiseSCM/Release' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot '../../bin/TortoiseSCM/packages' }
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
$output = Assert-TscmPlainPath $OutputDirectory
[IO.Directory]::CreateDirectory($output) | Out-Null
if ([string]::IsNullOrWhiteSpace($PackageArchive)) {
    if ([string]::IsNullOrWhiteSpace($BeyondCompareDirectory) -and [string]::IsNullOrWhiteSpace($TortoiseToolsDirectory)) { throw 'Provide bundled Tortoise tools or Beyond Compare runtime.' }
    $PackageArchive = & (Join-Path $PSScriptRoot 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory $output -Version $Version -BeyondCompareDirectory $BeyondCompareDirectory -TortoiseToolsDirectory $TortoiseToolsDirectory
}
$archivePath = Assert-TscmPlainPath $PackageArchive
$stage = Join-Path $output ('.setup-verify-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.TrimEnd('/')
            if (-not $seen.Add($relative)) { throw 'Duplicate path in package ZIP.' }
            $destination = Join-TscmOwnedPath $stage $relative
            if ($entry.FullName.EndsWith('/')) { [IO.Directory]::CreateDirectory($destination) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
        }
    } finally { $archive.Dispose() }
    $manifest = Read-TscmManifest $stage -VerifyFiles
    $hasNative = @($manifest.files | Where-Object path -eq 'Tools/TortoiseGit/TortoiseGitMerge.exe').Count -eq 1
    $requiredTools = if ($hasNative) { @('Tools/TortoiseGit/TortoiseGitMerge.exe', 'Tools/TortoiseGit/TortoiseGitUDiff.exe', 'Tools/TortoiseGit/LICENSE.txt', 'Tools/TortoiseGit/TortoiseGit-source.zip') }
        else { @('Tools/BeyondCompare/BCompare.exe', 'Tools/BeyondCompare/BComp.exe', 'Tools/BeyondCompare/License.html') }
    foreach ($relative in $requiredTools) {
        if (@($manifest.files | Where-Object path -eq $relative).Count -ne 1) { throw ('Installer must contain verified tool runtime: ' + $relative) }
    }
    $Version = [string]$manifest.version
    if ($Version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Package version cannot be used as an installer version.' }
    $suffix = if ($TestSetup) { '-Setup-Test.exe' } else { '-Setup.exe' }
    $setup = Join-Path $output ('TortoiseSCM-' + $Version + '-windows-x64' + $suffix)
    if (Test-Path -LiteralPath $setup) { throw ('Installer already exists: ' + $setup) }
    if ([string]::IsNullOrWhiteSpace($IsccPath)) { $IsccPath = & (Join-Path $PSScriptRoot 'Get-SetupCompiler.ps1') }
    $compiler = Assert-TscmPlainPath $IsccPath
    $payloadBytes = [long](($manifest.files | Measure-Object -Property length -Sum).Sum)
    $arguments = @('/Qp', ('/DPackageArchive=' + $archivePath), ('/DPackageVersion=' + $Version), ('/DSetupOutput=' + $output), ('/DInstalledPayloadSize=' + $payloadBytes))
    if ($TestSetup) { $arguments += '/DTestSetup=1' }
    $arguments += (Join-Path $PSScriptRoot 'Setup.iss')
    & $compiler @arguments | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Inno Setup compilation failed.' }
    (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($setup) | Set-Content -LiteralPath ($setup + '.sha256') -Encoding ASCII
    Write-Output $setup
} finally {
    $stage = Assert-TscmPlainPath $stage
    if (-not $stage.StartsWith($output.TrimEnd('\') + '\.setup-verify-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe verification directory cleanup.' }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
