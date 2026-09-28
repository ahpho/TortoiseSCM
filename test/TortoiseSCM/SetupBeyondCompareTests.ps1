# Isolated installer configuration migration tests; no user configuration is written.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$scripts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\contrib\tortoisescm'))
. (Join-Path $scripts 'Package.Common.ps1')
. (Join-Path $scripts 'SetupBeyondCompare.ps1')
$script:assertions = 0
function Assert([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
    Write-Output ('PASS: ' + $Message)
}
function New-FixtureVersion([string]$Root, [string]$Version) {
    $directory = Join-Path $Root ('versions\' + $Version)
    [IO.Directory]::CreateDirectory((Join-Path $directory 'Tools\BeyondCompare')) | Out-Null
    $files = foreach ($name in @('TortoiseSCM.exe', 'TortoiseSCMShell.dll', 'Install.ps1', 'Uninstall.ps1', 'Package.Common.ps1', 'Register-Shell.ps1', 'Unregister-Shell.ps1', 'LICENSE', 'Tools/BeyondCompare/BComp.exe', 'Tools/BeyondCompare/BCompare.exe', 'Tools/BeyondCompare/License.html')) {
        $path = Join-Path $directory $name
        [IO.File]::WriteAllText($path, ('inert fixture: ' + $name))
        [pscustomobject]@{ path = $name; length = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
    Write-TscmJson (Join-Path $directory 'package-manifest.json') ([ordered]@{ schemaVersion = 1; product = 'TortoiseSCM'; architecture = 'x64'; version = $Version; files = @($files) })
    Write-TscmJson (Join-Path $directory '.tortoisescm-install.json') ([ordered]@{ schemaVersion = 1; product = 'TortoiseSCM'; installRoot = $Root; versionDirectory = $directory; packageManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $directory 'package-manifest.json') -Algorithm SHA256).Hash })
    return $directory
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('TSCM-setup-bc-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
    $root = Join-Path $fixture 'install root'
    $old = New-FixtureVersion $root 'old-version'
    $current = New-FixtureVersion $root 'new-version'
    $settings = Join-Path $fixture 'settings.xml'
    $oldTool = Join-Path $old 'Tools\BeyondCompare\BComp.exe'
    function Write-Settings([string]$Path, [string]$Element = 'BeyondComparePath') {
        $escaped = [Security.SecurityElement]::Escape($Path)
        [IO.File]::WriteAllText($settings, ('<?xml version="1.0" encoding="utf-8"?><TortoiseSCM>' + "`r`n  <!-- keep this -->`r`n  " + '<CmPath>custom cm.exe</CmPath><TimeoutSeconds>93</TimeoutSeconds><Custom><Other>retain &amp; value</Other></Custom><' + $Element + '>' + $escaped + '</' + $Element + '></TortoiseSCM>'), (New-Object Text.UTF8Encoding($false)))
    }
    function Invoke-Migration { Update-TscmSetupBeyondCompare -InstallRoot $root -VersionDirectory $current -ConfigPath $settings }
    function Assert-Preserved([string]$Message, [bool]$Warn = $false) {
        $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($settings))
        $result = Invoke-Migration
        Assert (-not $result.changed -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($settings)) -ceq $before) $Message
        Assert (([string]::IsNullOrWhiteSpace($result.warning)) -ne $Warn) ($Message + ' (warning result)')
    }
    $missing = Invoke-Migration
    Assert (-not $missing.changed -and -not $missing.warning -and -not [IO.File]::Exists($settings)) 'Fresh installation needs no configuration file'
    $portable = Update-TscmSetupBeyondCompare -InstallRoot 'invalid' -VersionDirectory 'invalid' -NoRegister
    Assert (-not $portable.changed -and -not $portable.warning) 'NoRegister without explicit test configuration skips all settings access'
    Write-Settings ''
    Assert-Preserved 'Automatic detection remains unchanged'
    Write-Settings $oldTool
    $result = Invoke-Migration
    Assert ($result.changed -and -not $result.warning) 'Owned old bundle selection becomes automatic after upgrade'
    $xml = [xml][IO.File]::ReadAllText($settings)
    Assert ($xml.TortoiseSCM.BeyondComparePath -eq '') 'New automatic field is empty'
    Assert ($xml.TortoiseSCM.CmPath -eq 'custom cm.exe' -and $xml.TortoiseSCM.TimeoutSeconds -eq '93' -and $xml.TortoiseSCM.Custom.Other -eq 'retain & value' -and [IO.File]::ReadAllText($settings).Contains('<!-- keep this -->')) 'Unrelated XML fields and comments are preserved'
    Assert-Preserved 'Migration is idempotent'
    Write-Settings (Join-Path $old 'Tools\BeyondCompare\BCompare.exe')
    Assert ((Invoke-Migration).changed) 'GUI executable alias is recognized'
    Write-Settings $oldTool 'MergeToolPath'
    Assert ((Invoke-Migration).changed) 'Legacy bundled merge path gains explicit automatic setting'
    $xml = [xml][IO.File]::ReadAllText($settings)
    Assert ($xml.TortoiseSCM.MergeToolPath -eq $oldTool -and $xml.SelectNodes('/TortoiseSCM/BeyondComparePath').Count -eq 1) 'Legacy dormant path is retained'
    Write-Settings $oldTool 'DiffToolPath'
    Assert ((Invoke-Migration).changed) 'Legacy bundled diff path is recognized'
    Write-Settings (Join-Path $current 'Tools\BeyondCompare\BComp.exe')
    Assert ((Invoke-Migration).changed) 'Explicit current bundle selection is safe for future upgrades'
    Write-Settings (Join-Path $fixture 'external\BComp.exe')
    Assert-Preserved 'External custom tool, even stale, remains authoritative'
    $other = New-FixtureVersion (Join-Path $fixture 'different installation') 'other'
    Write-Settings (Join-Path $other 'Tools\BeyondCompare\BComp.exe')
    Assert-Preserved 'Different installation root remains user controlled'
    Write-Settings (Join-Path $old 'CustomTools\BeyondCompare\BComp.exe')
    Assert-Preserved 'Unowned tool below version directory is preserved'
    Write-Settings 'BComp.exe'
    Assert-Preserved 'Relative custom setting is preserved'
    Write-Settings $oldTool
    $recordPath = Join-Path $old '.tortoisescm-install.json'
    $recordBytes = [IO.File]::ReadAllBytes($recordPath)
    [IO.File]::Delete($recordPath)
    Assert-Preserved 'Directory without installation ownership is preserved' $true
    [IO.File]::WriteAllBytes($recordPath, $recordBytes)
    $manifestPath = Join-Path $old 'package-manifest.json'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    [IO.File]::AppendAllText($manifestPath, ' ')
    Assert-Preserved 'Manifest hash mismatch is preserved' $true
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    $newTool = Join-Path $current 'Tools\BeyondCompare\BComp.exe'
    $toolBytes = [IO.File]::ReadAllBytes($newTool)
    [IO.File]::WriteAllText($newTool, 'damaged')
    Assert-Preserved 'Damaged new runtime cannot replace existing choice' $true
    [IO.File]::WriteAllBytes($newTool, $toolBytes)
    [IO.File]::Delete($oldTool)
    Assert ((Invoke-Migration).changed) 'Stale bundled executable recovers when ownership metadata is intact'
    [IO.File]::WriteAllText($settings, '<!DOCTYPE TortoiseSCM [<!ENTITY value SYSTEM "file:///unrelated">]><TortoiseSCM><BeyondComparePath>&value;</BeyondComparePath></TortoiseSCM>')
    Assert-Preserved 'DTD settings are rejected without changing bytes' $true
    [IO.File]::WriteAllText($settings, '<broken')
    Assert-Preserved 'Malformed settings do not fail installation' $true
    Write-Settings $oldTool
    $locked = [IO.File]::Open($settings, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $result = Invoke-Migration
        Assert (-not $result.changed -and $result.warning) 'Locked settings return a warning without throwing'
    } finally { $locked.Dispose() }
    Assert (@(Get-ChildItem -LiteralPath $fixture -Filter '*.tmp' -File).Count -eq 0) 'No temporary configuration files remain'
    Write-Output "PASS: $script:assertions setup Beyond Compare assertions"
} finally {
    $checked = Assert-TscmPlainPath $fixture
    if (-not $checked.StartsWith([IO.Path]::GetTempPath().TrimEnd('\') + '\TSCM-setup-bc-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}
