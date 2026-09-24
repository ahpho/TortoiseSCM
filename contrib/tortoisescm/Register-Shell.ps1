[CmdletBinding()]
param(
    [string]$BinaryDirectory = (Join-Path $PSScriptRoot '..\..\bin\TortoiseSCM\Release')
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Run this script using 64-bit PowerShell.' }
$directory = (Resolve-Path -LiteralPath $BinaryDirectory).ProviderPath
$dll = Join-Path $directory 'TortoiseSCMShell.dll'
$exe = Join-Path $directory 'TortoiseSCM.exe'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Missing shell extension: $dll" }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing application: $exe" }
$clsid = '{B1DA45F9-4CD4-4857-A591-96B06953A0D2}'
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
} finally { $classes.Dispose() }
if (-not ('TortoiseSCM.ShellChange' -as [type])) {
    Add-Type -TypeDefinition 'namespace TortoiseSCM { public static class ShellChange { [System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, System.IntPtr a, System.IntPtr b); } }'
}
[TortoiseSCM.ShellChange]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output "Registered TortoiseSCM for the current user: $dll"
Write-Output 'Windows 11: use Show more options. If Explorer keeps a previous DLL loaded, sign out and sign in before replacing it.'
