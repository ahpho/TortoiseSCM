# Optional installer migration for a previously selected bundled tool. GPL-2.0-or-later.
# Dot-source Package.Common.ps1 before calling this function. No global settings
# are touched until a verified installation and its owned tool path are matched.
function Update-TscmSetupBeyondCompare {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$VersionDirectory,
        [Alias('SettingsPath')][string]$ConfigPath,
        [switch]$NoRegister
    )
    $result = [pscustomobject]@{ changed = $false; warning = '' }
    $temporary = $null
    try {
        # Portable/test installation must never rewrite real user settings.
        if ($NoRegister -and [string]::IsNullOrWhiteSpace($ConfigPath)) { return $result }
        if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
            $ConfigPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TortoiseSCM\settings.xml'
        }
        $settings = Assert-TscmPlainPath $ConfigPath
        if (-not [IO.File]::Exists($settings)) { return $result }
        if ((Get-Item -LiteralPath $settings).Length -gt 1MB) { throw 'Settings file is too large.' }
        $before = [IO.File]::ReadAllBytes($settings)
        $xml = New-Object Xml.XmlDocument
        $xml.PreserveWhitespace = $true
        $xml.XmlResolver = $null
        $readerSettings = New-Object Xml.XmlReaderSettings
        $readerSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $readerSettings.XmlResolver = $null
        $stream = New-Object IO.MemoryStream(,$before)
        $reader = [Xml.XmlReader]::Create($stream, $readerSettings)
        try { $xml.Load($reader) } finally { $reader.Dispose(); $stream.Dispose() }
        if (-not $xml.DocumentElement -or $xml.DocumentElement.Name -ne 'TortoiseSCM' -or $xml.DocumentElement.NamespaceURI) { throw 'Invalid TortoiseSCM settings.' }
        $nodes = $xml.SelectNodes('/TortoiseSCM/BeyondComparePath')
        if ($nodes.Count -gt 1) { throw 'Duplicate Beyond Compare settings.' }
        $node = $xml.SelectSingleNode('/TortoiseSCM/BeyondComparePath')
        $configured = if ($node) { $node.InnerText } else { '' }
        if (-not $node) {
            # Match the application's legacy migration order, without changing
            # dormant fields or overriding an explicit automatic setting.
            foreach ($field in @('MergeToolPath', 'DiffToolPath')) {
                $legacy = $xml.SelectSingleNode('/TortoiseSCM/' + $field)
                if ($legacy -and [IO.Path]::IsPathRooted($legacy.InnerText) -and [IO.Path]::GetFileName($legacy.InnerText) -in @('BComp.exe', 'BCompare.exe')) {
                    $configured = $legacy.InnerText
                    break
                }
            }
        }
        if ([string]::IsNullOrWhiteSpace($configured)) { return $result }
        $configured = $configured.Trim()
        if (-not [IO.Path]::IsPathRooted($configured) -or [IO.Path]::GetPathRoot($configured).Length -lt 3) { return $result }
        $tool = [IO.Path]::GetFullPath($configured)
        if ([IO.Path]::GetFileName($tool) -notin @('BComp.exe', 'BCompare.exe')) { return $result }
        $root = Assert-TscmPlainPath $InstallRoot
        $versions = Join-TscmOwnedPath $root 'versions'
        $old = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($tool)))
        if ([IO.Path]::GetDirectoryName($old) -ne $versions -or $tool -ne (Join-Path $old ('Tools\BeyondCompare\' + [IO.Path]::GetFileName($tool)))) { return $result }
        $current = Assert-TscmPlainPath $VersionDirectory
        if ([IO.Path]::GetDirectoryName($current) -ne $versions) { throw 'New version is not owned by this installation root.' }
        foreach ($directory in @($old, $current)) {
            $manifest = Read-TscmManifest $directory
            $record = [IO.File]::ReadAllText((Join-TscmOwnedPath $directory '.tortoisescm-install.json')) | ConvertFrom-Json
            $manifestHash = (Get-FileHash -LiteralPath (Join-TscmOwnedPath $directory 'package-manifest.json') -Algorithm SHA256).Hash
            if ($record.schemaVersion -ne 1 -or $record.product -ne 'TortoiseSCM' -or $record.installRoot -ne $root -or $record.versionDirectory -ne $directory -or $record.packageManifestSha256 -ne $manifestHash) { throw 'Installation ownership record does not match the package.' }
            foreach ($name in @('Tools/BeyondCompare/BComp.exe', 'Tools/BeyondCompare/BCompare.exe')) {
                if (@($manifest.files | Where-Object { $_.path -eq $name }).Count -ne 1) { throw 'The package does not own a complete bundled Beyond Compare runtime.' }
            }
        }
        Read-TscmManifest $current -VerifyFiles | Out-Null
        if (-not $node) {
            $node = $xml.CreateElement('BeyondComparePath')
            [void]$xml.DocumentElement.AppendChild($node)
        }
        $node.InnerText = ''
        $temporary = $settings + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $xml.Save($temporary)
        # Do not replace settings that the running application has just saved.
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($settings)) -cne [Convert]::ToBase64String($before)) { throw 'Settings changed during installation; the newer settings were preserved.' }
        [IO.File]::Replace($temporary, $settings, [System.Management.Automation.Language.NullString]::Value)
        $result.changed = $true
    } catch {
        $result.warning = 'Beyond Compare settings were preserved: ' + $_.Exception.Message
    } finally {
        if ($temporary -and [IO.File]::Exists($temporary)) {
            try { [IO.File]::Delete($temporary) } catch { }
        }
    }
    return $result
}
