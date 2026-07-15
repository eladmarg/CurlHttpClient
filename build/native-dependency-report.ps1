# Emits a JSON report of curl_http_bridge.dll's imported DLLs + exported
# functions (dumpbin), used both as evidence and by the WS2012R2 checklist.
param(
    [Parameter(Mandatory = $true)][string]$DllPath,
    [Parameter(Mandatory = $true)][string]$OutFile
)

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath | Select-Object -First 1
$dumpbin = Get-ChildItem "$vsPath\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe" |
    Select-Object -First 1 -ExpandProperty FullName

$imports = & $dumpbin /DEPENDENTS $DllPath |
    Where-Object { $_ -match '^\s+\S+\.dll\s*$' } |
    ForEach-Object { $_.Trim() }

$exports = & $dumpbin /EXPORTS $DllPath |
    Select-String -Pattern 'curl_bridge_\w+' |
    ForEach-Object { ($_ -split '\s+' | Where-Object { $_ -match 'curl_bridge_' })[0] } |
    Sort-Object -Unique

$allowed = @('KERNEL32.dll','ADVAPI32.dll','USER32.dll','WS2_32.dll',
    'IPHLPAPI.DLL','CRYPT32.dll','BCRYPT.dll','Secur32.dll','NORMALIZ.dll')
$disallowed = $imports | Where-Object { $allowed -notcontains $_ }

$report = [ordered]@{
    dll = (Split-Path -Leaf $DllPath)
    sha256 = (Get-FileHash $DllPath -Algorithm SHA256).Hash
    imports = $imports
    ws2012r2Compatible = ($disallowed.Count -eq 0)
    disallowedImports = $disallowed
    exports = $exports
    capturedUtc = (Get-Date).ToUniversalTime().ToString('o')
}

New-Item -ItemType Directory -Force (Split-Path -Parent $OutFile) | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -Path $OutFile -Encoding UTF8
Write-Host "Native dependency report written to $OutFile"
if ($disallowed) {
    Write-Error "Disallowed imports present: $($disallowed -join ', ')"
    exit 1
}
