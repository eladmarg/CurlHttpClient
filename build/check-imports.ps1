# Windows Server 2012 R2 compatibility gate.
#
# Statically-linked binaries only fail on an old OS at LOAD TIME, when an
# imported API is missing — invisible on the build machine. This script fails
# the build if curl_http_bridge.dll imports any DLL outside the set that
# shipped with Windows 8.1 / Server 2012 R2 (in particular: api-ms-win-*
# umbrella DLLs and vcruntime/msvcp, which would mean the CRT is no longer
# statically linked).
param(
    [Parameter(Mandatory = $true)][string]$DllPath
)

$allowed = @(
    'KERNEL32.dll', 'ADVAPI32.dll', 'USER32.dll',
    'WS2_32.dll', 'IPHLPAPI.DLL',
    'CRYPT32.dll', 'BCRYPT.dll', 'Secur32.dll', 'NORMALIZ.dll'
)

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath | Select-Object -First 1
$dumpbin = Get-ChildItem "$vsPath\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe" |
    Select-Object -First 1 -ExpandProperty FullName

$imports = & $dumpbin /DEPENDENTS $DllPath |
    Where-Object { $_ -match '^\s+\S+\.dll\s*$' } |
    ForEach-Object { $_.Trim() }

$violations = $imports | Where-Object { $allowed -notcontains $_ }

Write-Host "Imports of $(Split-Path -Leaf $DllPath):"
$imports | ForEach-Object { Write-Host "  $_" }

if ($violations) {
    Write-Error ("WS2012R2 compatibility gate FAILED. Disallowed imports: " +
        ($violations -join ', ') +
        ". Either an API newer than Windows 8.1 crept in, or the CRT is no longer statically linked.")
    exit 1
}
Write-Host "OK: all imports are Windows 8.1 / Server 2012 R2 system DLLs." -ForegroundColor Green
exit 0
