[CmdletBinding()]
param(
    [string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release'),
    [Alias('MachineOverlays')][switch]$EnableMachineOverlays
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Run this script using 64-bit PowerShell.' }
if ($EnableMachineOverlays -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '-EnableMachineOverlays requires an elevated 64-bit PowerShell because Windows discovers overlays under HKLM. No registration was changed.'
}
$directory = (Resolve-Path -LiteralPath $BinaryDirectory).ProviderPath
$dll = Join-Path $directory 'TortoiseSCMShell.dll'
$exe = Join-Path $directory 'TortoiseSCM.exe'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Missing shell extension: $dll" }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing application: $exe" }
$clsid = '{B1DA45F9-4CD4-4857-A591-96B06953A0D2}'
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
function Register-OverlayClasses([Microsoft.Win32.RegistryKey]$ClassesKey) {
    foreach ($overlay in $overlays) {
        $class = $ClassesKey.CreateSubKey("CLSID\$($overlay.Id)")
        try { $class.SetValue('', $overlay.Name) } finally { $class.Dispose() }
        $server = $ClassesKey.CreateSubKey("CLSID\$($overlay.Id)\InprocServer32")
        try { $server.SetValue('', $dll); $server.SetValue('ThreadingModel', 'Apartment') } finally { $server.Dispose() }
    }
}
$classes = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes')
try {
    $class = $classes.CreateSubKey("CLSID\$clsid")
    try { $class.SetValue('', 'TortoiseSCM Plastic SCM context menu') } finally { $class.Dispose() }
    $server = $classes.CreateSubKey("CLSID\$clsid\InprocServer32")
    try { $server.SetValue('', $dll); $server.SetValue('ThreadingModel', 'Apartment') } finally { $server.Dispose() }
    foreach ($kind in @('*', 'Directory', 'Directory\Background')) {
        $handler = $classes.CreateSubKey("$kind\shellex\ContextMenuHandlers\TortoiseSCM")
        try { $handler.SetValue('', $clsid) } finally { $handler.Dispose() }
    }
    Register-OverlayClasses $classes
} finally { $classes.Dispose() }
# Microsoft documents machine-scope overlay discovery. Per-user COM registration
# above supports activation/tests; it does not claim Explorer will load HKCU overlays.
# https://learn.microsoft.com/en-us/windows/win32/shell/how-to-register-icon-overlay-handlers
if ($EnableMachineOverlays) {
    $machineClasses = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('Software\Classes')
    try { Register-OverlayClasses $machineClasses } finally { $machineClasses.Dispose() }
    $overlayRoot = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers')
    try {
        foreach ($overlay in $overlays) {
            $key = $overlayRoot.CreateSubKey($overlay.Name)
            try {
                $existing = $key.GetValue('')
                if ($existing -and $existing -ne $overlay.Id) { throw "Overlay name already belongs to another handler: $($overlay.Name)" }
                $key.SetValue('', $overlay.Id)
            } finally { $key.Dispose() }
        }
    } finally { $overlayRoot.Dispose() }
    $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try { $run.SetValue('TortoiseSCMCache', ('"' + $exe + '" --cache-worker')) } finally { $run.Dispose() }
    Start-Process -FilePath $exe -ArgumentList '--cache-worker' -WindowStyle Hidden
    Write-Output ('Registered ' + $overlays.Count + ' machine overlay identifiers. Other handlers were not removed or reordered; Windows overlay slot limits may prevent display.')
} else {
    Write-Output 'Overlay COM classes registered for this user. To enable Explorer overlay discovery, rerun elevated with -EnableMachineOverlays.'
}
if (-not ('TortoiseSCM.ShellChange' -as [type])) {
    Add-Type -TypeDefinition 'namespace TortoiseSCM { public static class ShellChange { [System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, System.IntPtr a, System.IntPtr b); } }'
}
[TortoiseSCM.ShellChange]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output "Registered TortoiseSCM for the current user: $dll"
Write-Output 'Windows 11: use Show more options. If Explorer keeps a previous DLL loaded, sign out and sign in before replacing it.'
