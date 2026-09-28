[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$ExpectedBinaryDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
. (Join-Path $PSScriptRoot 'ModernMenu.Common.ps1')
$mutex = Enter-TscmInstallMutex
try {
    $directory = Assert-TscmPlainPath $ExpectedBinaryDirectory
    if ($PSCmdlet.ShouldProcess($directory, 'Remove only the modern Explorer registration pointing at this directory')) {
        Remove-TscmModernRegistration $directory
    }
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
