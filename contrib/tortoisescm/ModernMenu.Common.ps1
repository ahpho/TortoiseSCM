# Optional unsigned Windows 11 preview identity. Never alters certificate trust or Developer Mode.
Set-StrictMode -Version Latest
$script:TscmModernName = 'TortoiseSCM.ModernMenu.Preview'
$script:TscmModernPublisher = 'CN=TortoiseSCM Preview, OID.2.25.311729368913984317654407730594956997722=1'

function Assert-TscmModernSupport {
    if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit Windows PowerShell for the modern Explorer menu.' }
    $build = [int](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
    $command = Get-Command Add-AppxPackage -ErrorAction SilentlyContinue
    if ($build -lt 22000 -or -not $command -or -not $command.Parameters.ContainsKey('AllowUnsigned') -or -not $command.Parameters.ContainsKey('ExternalLocation')) {
        throw 'The optional modern Explorer menu requires Windows 11 and Add-AppxPackage with AllowUnsigned and ExternalLocation support.'
    }
    if (-not (Test-TscmAdministrator)) { throw 'Unsigned modern menu preview registration requires elevated Windows PowerShell because its manifest declares executable activation. Use the classic menu without -EnableModernMenu, or run this optional preview elevated. Certificate trust and Developer Mode are not changed.' }
}

function Get-TscmModernRegistration {
    $packages = @(Get-AppxPackage -Name $script:TscmModernName -ErrorAction Stop)
    if ($packages.Count -eq 0) { return $null }
    if ($packages.Count -ne 1 -or $packages[0].Publisher -ne $script:TscmModernPublisher -or [string]$packages[0].Architecture -ne 'X64') {
        throw 'Unexpected modern menu package identity. Existing registration was not changed.'
    }
    [Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime] | Out-Null
    $manager = New-Object Windows.Management.Deployment.PackageManager
    $package = $manager.FindPackageForUser('', $packages[0].PackageFullName)
    if (-not $package -or -not $package.EffectiveExternalLocation) { throw 'Cannot establish the registered modern menu external location.' }
    $external = Assert-TscmPlainPath $package.EffectiveExternalLocation.Path
    return [pscustomobject]@{ packageFullName = $packages[0].PackageFullName; binaryDirectory = $external }
}

function Assert-TscmModernFiles([string]$BinaryDirectory) {
    $directory = Assert-TscmPlainPath $BinaryDirectory
    $manifest = Read-TscmManifest $directory -VerifyFiles
    foreach ($required in @('ModernMenu/TortoiseSCM.ModernMenu.msix', 'ModernMenu/AppxManifest.xml', 'ModernMenu/Assets/StoreLogo.png',
        'ModernMenu/Assets/Square44x44Logo.png', 'ModernMenu/Assets/Square150x150Logo.png', 'ModernMenu.Common.ps1', 'Register-ModernShell.ps1', 'Unregister-ModernShell.ps1')) {
        if (@($manifest.files | Where-Object path -EQ $required).Count -ne 1) { throw "Package lacks verified modern menu component: $required" }
    }
    # Verify the embedded manifest too, before replacing a previous registration.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-TscmOwnedPath $directory 'ModernMenu\TortoiseSCM.ModernMenu.msix'))
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if (-not $entry -or $entry.Length -gt 65536) { throw 'Invalid sparse package manifest.' }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $xmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        [xml]$xml = $xmlText
        if ($xml.Package.Identity.Name -ne $script:TscmModernName -or $xml.Package.Identity.Publisher -ne $script:TscmModernPublisher -or
            $xml.Package.Identity.ProcessorArchitecture -ne 'x64' -or $xml.Package.Identity.Version -ne '1.0.0.0') { throw 'Unexpected sparse package identity.' }
        $diskXml = [IO.File]::ReadAllText((Join-TscmOwnedPath $directory 'ModernMenu\AppxManifest.xml'))
        [xml]$expected = $diskXml
        if ($xml.OuterXml -cne $expected.OuterXml) { throw 'Sparse package and shipped manifest differ.' }
        if (@($archive.Entries | Where-Object { $_.FullName -match '(?i)\.(exe|dll|ps1)$' }).Count -ne 0) { throw 'Sparse identity package unexpectedly contains executable content.' }
    } finally { $archive.Dispose() }
    return $directory
}

function Add-TscmModernPackage([string]$BinaryDirectory) {
    $path = Join-TscmOwnedPath $BinaryDirectory 'ModernMenu\TortoiseSCM.ModernMenu.msix'
    Add-AppxPackage -Path $path -ExternalLocation $BinaryDirectory -AllowUnsigned -ErrorAction Stop
    $registered = Get-TscmModernRegistration
    if (-not $registered -or $registered.binaryDirectory -ne $BinaryDirectory) { throw 'Windows did not activate the expected modern menu external location.' }
    return $registered
}

function Remove-TscmModernRegistration([string]$ExpectedBinaryDirectory) {
    $expected = Assert-TscmPlainPath $ExpectedBinaryDirectory
    $current = Get-TscmModernRegistration
    if (-not $current -or $current.binaryDirectory -ne $expected) { return $false }
    Remove-AppxPackage -Package $current.packageFullName -ErrorAction Stop
    if (Get-TscmModernRegistration) { throw 'Modern menu registration was not removed.' }
    return $true
}

function Restore-TscmModernRegistration($PreviousRegistration, [string]$AttemptedBinaryDirectory) {
    $current = Get-TscmModernRegistration
    if ($PreviousRegistration -and $current -and $current.binaryDirectory -eq $PreviousRegistration.binaryDirectory) { return }
    if ($current -and $current.binaryDirectory -ne $AttemptedBinaryDirectory) { throw 'Another modern menu registration replaced this installation; rollback refused to overwrite it.' }
    if ($current) { Remove-TscmModernRegistration $AttemptedBinaryDirectory | Out-Null }
    if ($PreviousRegistration) {
        $previous = Assert-TscmModernFiles $PreviousRegistration.binaryDirectory
        Add-TscmModernPackage $previous | Out-Null
    }
}

function Register-TscmModernMenu([string]$BinaryDirectory, [string]$ExpectedPreviousBinaryDirectory) {
    Assert-TscmModernSupport
    $directory = Assert-TscmModernFiles $BinaryDirectory
    $previous = Get-TscmModernRegistration
    if ($previous -and $previous.binaryDirectory -eq $directory) { return $previous }
    if ($previous) {
        if ([string]::IsNullOrWhiteSpace($ExpectedPreviousBinaryDirectory) -or $previous.binaryDirectory -ne (Assert-TscmPlainPath $ExpectedPreviousBinaryDirectory)) {
            throw 'Modern menu belongs to another directory. Supply its exact -ExpectedPreviousBinaryDirectory to replace it.'
        }
        Assert-TscmModernFiles $previous.binaryDirectory | Out-Null
    }
    try {
        try { return Add-TscmModernPackage $directory }
        catch {
            # Windows rejects moving an already installed identity/version with
            # 0x80073CFB. Only that failure permits remove/retry; arbitrary failures
            # preserve the old registration. Both old/new sources were verified.
            if (-not $previous -or ([string]$_ -notmatch '(?i)0x80073CFB' -and $_.Exception.HResult -ne -2147009285)) { throw }
            Remove-TscmModernRegistration $previous.binaryDirectory | Out-Null
            return Add-TscmModernPackage $directory
        }
    } catch {
        $failure = $_
        try { Restore-TscmModernRegistration $previous $directory }
        catch { throw "Modern menu registration failed: $failure. Rollback also failed: $_. Keep both installation directories for recovery." }
        throw "Modern menu preview registration failed: $failure. Windows policy may require elevation or disallow unsigned packages; no certificate trust or Developer Mode settings were changed."
    }
}
