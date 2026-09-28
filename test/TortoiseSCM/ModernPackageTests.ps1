# Tests sparse identity wiring and deployment transactions without changing Appx registration.
[CmdletBinding()]
param([string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release'))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scripts = Join-Path $repo 'contrib\tortoisescm'
. (Join-Path $scripts 'Package.Common.ps1')
. (Join-Path $scripts 'ModernMenu.Common.ps1')
$script:assertions = 0
function Assert([bool]$Condition, [string]$Description) { $script:assertions++; if (-not $Condition) { throw $Description }; Write-Host "PASS: $Description" }
function Reject([scriptblock]$Action, [string]$Description) { $failed = $false; try { & $Action | Out-Null } catch { $failed = $true }; Assert $failed $Description }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('TSCM-modern-package-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$before = @(Get-AppxPackage -Name 'TortoiseSCM.ModernMenu.Preview' | Select-Object PackageFullName, InstallLocation) | ConvertTo-Json
try {
    $zip = & (Join-Path $scripts 'Package.ps1') -BinaryDirectory $BinaryDirectory -OutputDirectory (Join-Path $fixture 'packages') -Version '0.1.0-modern-test'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Join-Path $fixture 'package'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $package)
    Assert ((Assert-TscmModernFiles $package) -eq $package) 'Sparse archive and every shipped component pass integrity validation'
    [xml]$xml = [IO.File]::ReadAllText((Join-Path $package 'ModernMenu\AppxManifest.xml'))
    $ns = New-Object Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('d5', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/5')
    $ns.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
    $types = @($xml.SelectNodes('//d5:ItemType', $ns) | ForEach-Object Type)
    Assert ($types.Count -eq 3 -and '*' -in $types -and 'Directory' -in $types -and 'Directory\Background' -in $types) 'Sparse manifest covers files, directories and folder backgrounds'
    $class = $xml.SelectSingleNode('//com:Class', $ns)
    Assert ($class.Id -eq 'B1DA45F9-4CD4-4857-A591-96B06953A0DB' -and $class.Path -eq 'TortoiseSCMShell.dll' -and $class.ThreadingModel -eq 'STA') 'Packaged surrogate targets dedicated modern CLSID in the adjacent DLL'
    Assert (@($xml.SelectNodes('//d5:Verb', $ns) | Where-Object Clsid -NE $class.Id).Count -eq 0) 'Every context-menu verb resolves to the packaged COM class'
    Reject { & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot (Join-Path $fixture 'invalid') -EnableModernMenu -NoRegister } 'Modern menu cannot be silently enabled in a portable installation'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture 'invalid'))) 'Conflicting flags create no files'
    if (-not (Test-TscmAdministrator)) {
        Reject { & (Join-Path $package 'Install.ps1') -PackageDirectory $package -InstallRoot (Join-Path $fixture 'nonadmin') -EnableModernMenu } 'Unsigned executable-activation preview explains missing elevation before mutation'
        Assert (-not (Test-Path -LiteralPath (Join-Path $fixture 'nonadmin'))) 'Modern support preflight leaves installation root absent'
    }

    # Replace only the OS boundaries. Production ownership/retry/rollback code runs unchanged.
    function Assert-TscmModernSupport { }
    function Assert-TscmModernFiles([string]$BinaryDirectory) { return Assert-TscmPlainPath $BinaryDirectory }
    function Get-TscmModernRegistration { return $script:registration }
    function Add-AppxPackage {
        param($Path, $ExternalLocation, [switch]$AllowUnsigned, $ErrorAction)
        $script:addCount++
        if ($script:noOpAdd) { return }
        if ($script:failAllAdds -or ($script:failNextNewAdd -and -not $script:registration -and $ExternalLocation -eq $script:b)) { $script:failNextNewAdd = $false; throw '0x80073D2B denied' }
        if ($script:registration) { throw '0x80073CFB identity already registered' }
        $script:registration = [pscustomobject]@{ packageFullName = 'preview_1.0.0.0_x64'; binaryDirectory = $ExternalLocation }
    }
    function Remove-AppxPackage { param($Package, $ErrorAction); if ($script:failRemove) { throw 'Removal denied' }; $script:removeCount++; $script:registration = $null }
    $script:a = Join-Path $fixture 'A'; $script:b = Join-Path $fixture 'B'; $script:c = Join-Path $fixture 'C'
    $script:registration = $null; $script:addCount = 0; $script:removeCount = 0; $script:failNextNewAdd = $false; $script:failAllAdds = $false; $script:noOpAdd = $false; $script:failRemove = $false
    Register-TscmModernMenu $script:a | Out-Null
    Assert ($script:registration.binaryDirectory -eq $script:a -and $script:addCount -eq 1) 'Initial registration records expected external directory'
    Register-TscmModernMenu $script:a | Out-Null
    Assert ($script:addCount -eq 1) 'Re-registering same directory is idempotent'
    Reject { Register-TscmModernMenu $script:b } 'Replacing another directory requires exact previous ownership'
    Assert ($script:removeCount -eq 0) 'Missing ownership never unregisters the prior identity'
    $snapshotA = $script:registration
    Register-TscmModernMenu $script:b $script:a | Out-Null
    Assert ($script:registration.binaryDirectory -eq $script:b -and $script:removeCount -eq 1 -and $script:addCount -eq 3) 'Same-version 0x80073CFB failure removes old identity then registers new external location'
    Assert (-not (Remove-TscmModernRegistration $script:a) -and $script:registration.binaryDirectory -eq $script:b) 'Old-version uninstall cannot unregister the active newer version'
    Restore-TscmModernRegistration $snapshotA $script:b
    Assert ($script:registration.binaryDirectory -eq $script:a) 'Publication rollback helper restores exact previous external directory'
    $script:failNextNewAdd = $true
    Reject { Register-TscmModernMenu $script:b $script:a } 'Failure after same-version removal propagates'
    Assert ($script:registration.binaryDirectory -eq $script:a) 'Failure after old identity removal restores the original package'
    $removeBefore = $script:removeCount
    $script:failAllAdds = $true
    Reject { Register-TscmModernMenu $script:b $script:a } 'Unrelated deployment error propagates'
    Assert ($script:removeCount -eq $removeBefore -and $script:registration.binaryDirectory -eq $script:a) 'Unrelated deployment error never removes previous registration'
    $script:failAllAdds = $false
    $script:noOpAdd = $true
    Reject { Register-TscmModernMenu $script:b $script:a } 'Silent deployment no-op fails expected external directory verification'
    Assert ($script:registration.binaryDirectory -eq $script:a -and $script:removeCount -eq $removeBefore) 'Deployment no-op preserves previous registration'
    $script:noOpAdd = $false; $script:failRemove = $true
    Reject { Register-TscmModernMenu $script:b $script:a } 'Failed old identity removal propagates without installing new directory'
    Assert ($script:registration.binaryDirectory -eq $script:a) 'Failed removal keeps the old registration available'
    $script:failRemove = $false
    Remove-TscmModernRegistration $script:a | Out-Null
    Register-TscmModernMenu $script:b | Out-Null
    Restore-TscmModernRegistration $null $script:b
    Assert ($null -eq $script:registration) 'Initial-install rollback restores absence when no prior identity existed'
    $script:registration = [pscustomobject]@{ packageFullName = 'foreign'; binaryDirectory = $script:c }
    Reject { Restore-TscmModernRegistration $snapshotA $script:b } 'Rollback refuses an unexpected intervening external directory'
    Assert ($script:registration.binaryDirectory -eq $script:c) 'Rollback preserves intervening registration'
    $after = @(Get-AppxPackage -Name 'TortoiseSCM.ModernMenu.Preview' | Select-Object PackageFullName, InstallLocation) | ConvertTo-Json
    Assert ($before -ceq $after) 'Real Appx registration is unchanged throughout tests'
    Write-Output "PASS: $script:assertions modern package assertions"
} finally {
    $checked = Assert-TscmPlainPath $fixture
    if (-not $checked.StartsWith([IO.Path]::GetTempPath().TrimEnd('\') + '\TSCM-modern-package-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}
