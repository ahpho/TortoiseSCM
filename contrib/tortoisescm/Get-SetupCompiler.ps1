[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../../bin/TortoiseSCM/tools/inno-6.7.3'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
$target = Assert-TscmPlainPath $OutputDirectory
$compiler = Join-Path $target 'ISCC.exe'
if (Test-Path -LiteralPath $compiler -PathType Leaf) {
    $signature = Get-AuthenticodeSignature -LiteralPath $compiler
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B.V.') { throw 'Cached Inno Setup compiler vendor signature is not valid.' }
    Write-Output $compiler; return
}
$parent = Assert-TscmPlainPath ([IO.Path]::GetDirectoryName($target))
[IO.Directory]::CreateDirectory($parent) | Out-Null
$download = Join-Path $parent ('inno-download-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    Invoke-WebRequest -UseBasicParsing 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download
    $expected = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expected) { throw 'Inno Setup download hash did not match the pinned official release.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $download
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B.V.') { throw 'Inno Setup vendor signature is not valid.' }
    # Upstream /PORTABLE=1 disables uninstall registration, associations and shortcuts.
    $process = Start-Process -FilePath $download -ArgumentList @('/PORTABLE=1', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', ('/DIR="' + $target + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Could not extract the portable Inno Setup compiler.' }
    Write-Output $compiler
} finally {
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
}
