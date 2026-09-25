[CmdletBinding()]
param(
    [Alias('MachineOverlays')][switch]$RemoveMachineOverlays,
    [string]$ExpectedBinaryDirectory
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Run this script using 64-bit PowerShell.' }
if ($RemoveMachineOverlays -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '-RemoveMachineOverlays requires an elevated 64-bit PowerShell. No registration was changed.'
}
$clsid = '{B1DA45F9-4CD4-4857-A591-96B06953A0D2}'
$expectedDll = if ($ExpectedBinaryDirectory) { [IO.Path]::GetFullPath((Join-Path $ExpectedBinaryDirectory 'TortoiseSCMShell.dll')) } else { $null }
function Test-OwnedServer([Microsoft.Win32.RegistryKey]$ClassesKey, [string]$Id) {
    if (-not $expectedDll) { return $true }
    $server = $ClassesKey.OpenSubKey("CLSID\$Id\InprocServer32")
    if (-not $server) { return $false }
    try { return [string]::Equals([string]$server.GetValue(''), $expectedDll, [StringComparison]::OrdinalIgnoreCase) }
    finally { $server.Dispose() }
}
$overlays = @(
    @{ Name = 'TortoiseSCM Normal'; Id = '{B1DA45F9-4CD4-4857-A591-96B06953A0D3}' },
    @{ Name = 'TortoiseSCM Modified'; Id = '{B1DA45F9-4CD4-4857-A591-96B06953A0D4}' },
    @{ Name = 'TortoiseSCM Conflict'; Id = '{B1DA45F9-4CD4-4857-A591-96B06953A0D5}' },
    @{ Name = 'TortoiseSCM Added'; Id = '{B1DA45F9-4CD4-4857-A591-96B06953A0D6}' },
    @{ Name = 'TortoiseSCM Deleted'; Id = '{B1DA45F9-4CD4-4857-A591-96B06953A0D7}' },
    @{ Name = 'TortoiseSCM Ignored'; Id = '{B1DA45F9-4CD4-4857-A591-96B06953A0D8}' },
    @{ Name = 'TortoiseSCM Locked'; Id = '{B1DA45F9-4CD4-4857-A591-96B06953A0D9}' },
    @{ Name = 'TortoiseSCM Unversioned'; Id = '{B1DA45F9-4CD4-4857-A591-96B06953A0DA}' }
)
function Remove-OverlayClasses([Microsoft.Win32.RegistryKey]$ClassesKey) {
    foreach ($overlay in $overlays) {
        if (-not (Test-OwnedServer $ClassesKey $overlay.Id)) { continue }
        $key = $ClassesKey.OpenSubKey("CLSID\$($overlay.Id)")
        if ($key) {
            try { $isOurs = $key.GetValue('') -eq $overlay.Name } finally { $key.Dispose() }
            if ($isOurs) { $ClassesKey.DeleteSubKeyTree("CLSID\$($overlay.Id)", $false) }
        }
    }
}
$classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes', $true)
if ($classes) {
    try {
        if (Test-OwnedServer $classes $clsid) {
          $activeServer = $classes.OpenSubKey("CLSID\$clsid\InprocServer32")
          $activeDll = $null
          if ($activeServer) { try { $activeDll = [string]$activeServer.GetValue('') } finally { $activeServer.Dispose() } }
          if ($activeDll) {
            $activeExe = Join-Path ([IO.Path]::GetDirectoryName($activeDll)) 'TortoiseSCM.exe'
            $run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true)
            if ($run) {
              try {
                if ([string]::Equals([string]$run.GetValue('TortoiseSCMCache'), ('"' + $activeExe + '" --cache-worker'), [StringComparison]::OrdinalIgnoreCase)) {
                    $run.DeleteValue('TortoiseSCMCache', $false)
                }
              } finally { $run.Dispose() }
            }
            if (Test-Path -LiteralPath $activeExe -PathType Leaf) {
              $stopProcess = Start-Process -FilePath $activeExe -ArgumentList '--cache-stop' -WindowStyle Hidden -PassThru
              try { if (-not $stopProcess.WaitForExit(10000)) { Write-Warning 'Cache stop signal did not complete within ten seconds.' } } finally { $stopProcess.Dispose() }
            }
          }
          foreach ($kind in @('*', 'Directory', 'Directory\Background')) {
            $path = "$kind\shellex\ContextMenuHandlers\TortoiseSCM"
            $handler = $classes.OpenSubKey($path)
            if ($handler) {
                try { $isOurs = $handler.GetValue('') -eq $clsid } finally { $handler.Dispose() }
                if ($isOurs) { $classes.DeleteSubKeyTree($path, $false) }
            }
          }
          $classes.DeleteSubKeyTree("CLSID\$clsid", $false)
        }
        Remove-OverlayClasses $classes
    } finally { $classes.Dispose() }
}
if ($RemoveMachineOverlays) {
    $machineClasses = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('Software\Classes', $true)
    $overlayRoot = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers', $true)
    if ($overlayRoot) {
        try {
            foreach ($overlay in $overlays) {
                if (-not $machineClasses -or -not (Test-OwnedServer $machineClasses $overlay.Id)) { continue }
                $key = $overlayRoot.OpenSubKey($overlay.Name)
                if ($key) {
                    try { $isOurs = $key.GetValue('') -eq $overlay.Id } finally { $key.Dispose() }
                    if ($isOurs) { $overlayRoot.DeleteSubKeyTree($overlay.Name, $false) }
                }
            }
        } finally { $overlayRoot.Dispose() }
    }
    if ($machineClasses) { try { Remove-OverlayClasses $machineClasses } finally { $machineClasses.Dispose() } }
} else {
    Write-Output 'Machine overlay registration, if installed, remains. Remove it with elevated -RemoveMachineOverlays.'
}
if (-not ('TortoiseSCM.ShellChange' -as [type])) {
    Add-Type -TypeDefinition 'namespace TortoiseSCM { public static class ShellChange { [System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, System.IntPtr a, System.IntPtr b); } }'
}
[TortoiseSCM.ShellChange]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output 'Unregistration completed for matching TortoiseSCM registrations. Explorer may keep the DLL loaded until you sign out.'
