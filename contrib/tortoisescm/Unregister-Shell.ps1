[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Run this script using 64-bit PowerShell.' }
$clsid = '{B1DA45F9-4CD4-4857-A591-96B06953A0D2}'
$classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes', $true)
if ($classes) {
    try {
        foreach ($kind in @('*', 'Directory', 'Directory\Background')) {
            $path = "$kind\shellex\ContextMenuHandlers\TortoiseSCM"
            $handler = $classes.OpenSubKey($path)
            if ($handler) {
                try { $isOurs = $handler.GetValue('') -eq $clsid } finally { $handler.Dispose() }
                if ($isOurs) { $classes.DeleteSubKeyTree($path, $false) }
            }
        }
        $classes.DeleteSubKeyTree("CLSID\$clsid", $false)
    } finally { $classes.Dispose() }
}
if (-not ('TortoiseSCM.ShellChange' -as [type])) {
    Add-Type -TypeDefinition 'namespace TortoiseSCM { public static class ShellChange { [System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, System.IntPtr a, System.IntPtr b); } }'
}
[TortoiseSCM.ShellChange]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output 'Unregistered TortoiseSCM for the current user. Explorer may keep the DLL loaded until you sign out.'
