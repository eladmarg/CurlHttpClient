# Windows Server 2012 R2 on-target validation.
#
# Run on a CLEAN WS2012R2 x64 VM snapshot (no developer tooling) after
# deploying the published application + native assets. Produces a pass/fail
# checklist. See docs/deployment-ws2012r2.md for the manual TLS-endpoint steps.
#
#   .\validate-ws2012r2.ps1 -AppDir C:\deployed\app -SampleExe CurlHttpClient.Sample.exe
param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [string]$SampleExe = "CurlHttpClient.Sample.exe",
    [string]$ModernTlsUrl = "https://www.howsmyssl.com/a/check"
)

$ErrorActionPreference = "Stop"
$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$name, [scriptblock]$test) {
    try {
        $ok = & $test
        $status = if ($ok) { "PASS" } else { "FAIL" }
        $results.Add([pscustomobject]@{ Check = $name; Result = $status })
        Write-Host ("[{0}] {1}" -f $status, $name)
    } catch {
        $results.Add([pscustomobject]@{ Check = $name; Result = "FAIL"; Error = $_.Exception.Message })
        Write-Host ("[FAIL] {0}: {1}" -f $name, $_.Exception.Message)
    }
}

$osVersion = [System.Environment]::OSVersion.Version
Check "Running on Windows 6.3 (Server 2012 R2 / 8.1)" { $osVersion.Major -eq 6 -and $osVersion.Minor -eq 3 }

$nativeDll = Join-Path $AppDir "runtimes\win-x64\native\curl_http_bridge.dll"
Check "Native bridge DLL is deployed" { Test-Path $nativeDll }
Check "Bundled cacert.pem is deployed" { Test-Path (Join-Path $AppDir "runtimes\win-x64\native\cacert.pem") }

# Load-time proof: if the DLL imports an unsupported API, LoadLibrary fails here.
Check "Native DLL loads on this OS" {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Loader {
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);
}
"@
    $h = [Loader]::LoadLibrary($nativeDll)
    $h -ne [IntPtr]::Zero
}

$sample = Join-Path $AppDir $SampleExe
Check ".NET sample runs + reports OpenSSL backend" {
    $output = & $sample 2>&1 | Out-String
    $script:sampleOutput = $output
    $output -match "OpenSSL: True"
}

Check "TLS backend is OpenSSL (not Schannel)" {
    $script:sampleOutput -match "OpenSSL/3\."
}

Check "Modern-TLS endpoint reachable ($ModernTlsUrl)" {
    $output = & $sample $ModernTlsUrl 2>&1 | Out-String
    $output -match "200 OK"
}

# Certificate validation is alive: an untrusted endpoint must FAIL.
Check "Certificate validation rejects an untrusted endpoint" {
    $output = & $sample "https://expired.badssl.com/" 2>&1 | Out-String
    -not ($output -match "200 OK")
}

Write-Host ""
Write-Host "=== WS2012R2 validation summary ==="
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.Result -eq "FAIL" }).Count
$reportPath = Join-Path $AppDir "ws2012r2-validation.json"
$results | ConvertTo-Json -Depth 4 | Set-Content -Path $reportPath -Encoding UTF8
Write-Host "Report written to $reportPath"
if ($failed -gt 0) {
    Write-Error "$failed WS2012R2 validation check(s) failed."
    exit 1
}
Write-Host "All WS2012R2 validation checks passed."
