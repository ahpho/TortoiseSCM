[CmdletBinding(SupportsShouldProcess = $true)]
param([string]$BinaryDirectory = $PSScriptRoot, [string]$ExpectedPreviousBinaryDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
. (Join-Path $PSScriptRoot 'ModernMenu.Common.ps1')
$mutex = Enter-TscmInstallMutex
try {
    Assert-TscmModernSupport
    $directory = Assert-TscmModernFiles $BinaryDirectory
    if ($PSCmdlet.ShouldProcess($directory, 'Register optional unsigned Windows 11 preview Explorer menu for this user')) {
        Register-TscmModernMenu $directory $ExpectedPreviousBinaryDirectory
    }
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
