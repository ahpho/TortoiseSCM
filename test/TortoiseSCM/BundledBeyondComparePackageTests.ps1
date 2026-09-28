# Packaging tests use inert fixture bytes, never execute third-party tools or register Explorer extensions.
[CmdletBinding()]
param([string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scripts = Join-Path $repo 'contrib\tortoisescm'
. (Join-Path $scripts 'Package.Common.ps1')
$script:assertions = 0
function Assert([bool]$Condition, [string]$Description) {
    $script:assertions++
    if (-not $Condition) { throw $Description }
    Write-Host "PASS: $Description"
}
function Reject([scriptblock]$Action, [string]$Description) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert $rejected $Description
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('TSCM-bundled-bc-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$registrationBefore = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets $false)) | ConvertTo-Json -Depth 30
try {
    $runtime = Join-Path $fixture 'source runtime'
    [IO.Directory]::CreateDirectory($runtime) | Out-Null
    $required = @('BCompare.exe', 'BComp.exe', 'License.html')
    $allowed = $required + @('BComp.com', '7z.dll', 'BCUnRar.dll', 'mime.types', 'PdfToText.exe', 'XLS_to_TAB_Single.vbs')
    $excluded = @('Patch.exe', 'BC4Key.txt', 'BCShellEx64.dll', 'BCState.xml', 'BCSessions.xml', 'BCPreferences.xml', 'settings.xml')
    foreach ($name in ($allowed + $excluded)) { [IO.File]::WriteAllText((Join-Path $runtime $name), ('Fixture bytes: ' + $name)) }
    [IO.Directory]::CreateDirectory((Join-Path $runtime 'sessions')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $runtime 'sessions\private.xml'), 'private session')
    $output = Join-Path $fixture 'packages'
    foreach ($name in $required) {
        $path = Join-Path $runtime $name
        $original = [IO.File]::ReadAllBytes($path)
        Remove-Item -LiteralPath $path
        try {
            Reject { & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory $output -Version 'missing-runtime' -BeyondCompareDirectory $runtime } "Missing required runtime file is rejected: $name"
            Assert (-not (Test-Path -LiteralPath $output)) 'Invalid runtime creates no package output'
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    $link = Join-Path $fixture 'runtime-junction'
    New-Item -ItemType Junction -Path $link -Target $runtime | Out-Null
    try {
        Reject { & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory $output -Version 'linked-runtime' -BeyondCompareDirectory $link } 'Runtime directory junction is rejected'
    } finally { [IO.Directory]::Delete($link) }
    # A directory in place of an allowlisted file must not be recursively copied.
    $optional = Join-Path $runtime 'BComp.com'
    Remove-Item -LiteralPath $optional
    [IO.Directory]::CreateDirectory($optional) | Out-Null
    Reject { & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory $output -Version 'directory-runtime' -BeyondCompareDirectory $runtime } 'Allowlisted entry that is a directory is rejected'
    [IO.Directory]::Delete($optional)
    [IO.File]::WriteAllText($optional, 'Fixture bytes: BComp.com')

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory $output -Version '0.1.0-bundled-bc-test' -BeyondCompareDirectory $runtime
    $package = Join-Path $fixture 'unpacked'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $package)
    $manifest = Read-TscmManifest $package -VerifyFiles
    Assert (@($manifest.bundledTools).Count -eq 1 -and $manifest.bundledTools[0].license -eq 'Tools/BeyondCompare/License.html') 'Manifest identifies the separate Beyond Compare runtime and license'
    $toolEntries = @($manifest.files | Where-Object { $_.path.StartsWith('Tools/BeyondCompare/') })
    Assert ($toolEntries.Count -eq $allowed.Count) 'Manifest contains exactly the allowlisted runtime files'
    foreach ($name in $allowed) {
        $entry = @($toolEntries | Where-Object { $_.path -eq ('Tools/BeyondCompare/' + $name) })
        $source = Join-Path $runtime $name
        $target = Join-Path $package ('Tools\BeyondCompare\' + $name)
        Assert ($entry.Count -eq 1 -and $entry[0].length -eq (Get-Item -LiteralPath $source).Length -and $entry[0].sha256 -eq (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $entry[0].sha256) "Bundled bytes, length and SHA-256 match source: $name"
    }
    foreach ($name in $excluded) { Assert (-not (Test-Path -LiteralPath (Join-Path $package ('Tools\BeyondCompare\' + $name)))) "Personal or non-runtime file is excluded: $name" }
    Assert (-not (Test-Path -LiteralPath (Join-Path $package 'Tools\BeyondCompare\sessions'))) 'Runtime subdirectories and personal sessions are not copied'
    $readme = [IO.File]::ReadAllText((Join-Path $package 'README-PACKAGE.txt'))
    Assert ($readme.Contains('runtime is included') -and $readme.Contains('Tools/BeyondCompare/License.html') -and -not $readme.Contains('not bundled')) 'Bundled README describes included runtime and its separate license'
    Assert ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -eq ([IO.File]::ReadAllText($zip + '.sha256').Split(' ')[0])) 'Bundled archive checksum matches'

    $plainZip = & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory $output -Version '0.1.0-unbundled-bc-test'
    $plainPackage = Join-Path $fixture 'plain'
    [IO.Compression.ZipFile]::ExtractToDirectory($plainZip, $plainPackage)
    $plainManifest = Read-TscmManifest $plainPackage -VerifyFiles
    Assert (-not (Test-Path -LiteralPath (Join-Path $plainPackage 'Tools\BeyondCompare')) -and -not $plainManifest.PSObject.Properties['bundledTools']) 'Default package does not bundle a runtime or tool metadata'
    Assert ([IO.File]::ReadAllText((Join-Path $plainPackage 'README-PACKAGE.txt')).Contains('not bundled in this package')) 'Default README directs users to their separately installed Beyond Compare'

    $installRoot = Join-Path $fixture 'installation'
    $first = & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot $installRoot -NoRegister
    Read-TscmManifest $first.versionDirectory -VerifyFiles | Out-Null
    Assert (-not $first.registered -and (Test-Path -LiteralPath (Join-Path $first.versionDirectory 'Tools\BeyondCompare\BComp.exe'))) 'NoRegister installation includes verified bundled runtime'
    $userFile = Join-Path $first.versionDirectory 'Tools\BeyondCompare\my-session.xml'
    [IO.File]::WriteAllText($userFile, 'user owned')
    $modified = Join-Path $first.versionDirectory 'Tools\BeyondCompare\mime.types'
    [IO.File]::AppendAllText($modified, ' user edit')
    # A distinct version upgrades normally, retaining the old runtime while in use.
    $nextZip = & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory $output -Version '0.1.1-bundled-bc-test' -BeyondCompareDirectory $runtime
    $nextPackage = Join-Path $fixture 'next'
    [IO.Compression.ZipFile]::ExtractToDirectory($nextZip, $nextPackage)
    $second = & (Join-Path $nextPackage 'Install.ps1') -PackageDirectory $nextPackage -InstallRoot $installRoot -NoRegister
    Assert ($first.versionDirectory -ne $second.versionDirectory -and (Test-Path -LiteralPath $userFile)) 'New-version upgrade preserves old runtime and user additions'
    Read-TscmManifest $second.versionDirectory -VerifyFiles | Out-Null
    $partial = & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $first.versionDirectory -NoUnregister -WarningAction SilentlyContinue
    Assert (-not $partial.removed -and (Test-Path -LiteralPath $modified) -and (Test-Path -LiteralPath $userFile)) 'Uninstall preserves modified runtime files and unlisted user files'
    Assert (-not (Test-Path -LiteralPath (Join-Path $first.versionDirectory 'Tools\BeyondCompare\BComp.exe'))) 'Uninstall removes unchanged owned runtime files'
    $pointer = [IO.File]::ReadAllText((Join-Path $installRoot 'current-install.json')) | ConvertFrom-Json
    Assert ($pointer.versionDirectory -eq $second.versionDirectory) 'Uninstalling old bundle preserves newer active version'
    [IO.File]::Copy((Join-Path $runtime 'mime.types'), $modified, $true)
    $removed = & (Join-Path $package 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $first.versionDirectory -NoUnregister
    Assert ($removed.removed -and -not (Test-Path -LiteralPath $modified) -and (Test-Path -LiteralPath $userFile)) 'Retry removes restored owned runtime while preserving user additions'
    $completed = & (Join-Path $nextPackage 'Uninstall.ps1') -InstallRoot $installRoot -VersionDirectory $second.versionDirectory -NoUnregister
    Assert ($completed.removed -and -not (Test-Path -LiteralPath $second.versionDirectory)) 'Unmodified upgraded bundle uninstalls completely'
    $registrationAfter = @(Get-TscmRegistrySnapshot @(Get-TscmRegistryTargets $false)) | ConvertTo-Json -Depth 30
    Assert ($registrationBefore -ceq $registrationAfter) 'Actual Explorer registration is unchanged'
    Write-Output "PASS: $script:assertions bundled Beyond Compare package assertions"
} finally {
    $checked = Assert-TscmPlainPath $fixture
    if (-not $checked.StartsWith([IO.Path]::GetTempPath().TrimEnd('\') + '\TSCM-bundled-bc-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}
