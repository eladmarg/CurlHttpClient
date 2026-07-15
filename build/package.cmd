@echo off
setlocal
rem Builds the NuGet packages (managed lib + native assets under
rem runtimes/win-x64/native) and emits a SHA-256 manifest of native assets.
set REPO=%~dp0..
set NATIVE=%REPO%\native\CurlBridge\out\build\x64-windows-static-v142\curl_http_bridge.dll

if not exist "%NATIVE%" (
    echo ERROR: native bridge not built. Run build\build-native-x64.cmd first.
    exit /b 1
)

dotnet pack "%REPO%\src\CurlHttpClient" -c Release -o "%REPO%\artifacts" || exit /b 1
dotnet pack "%REPO%\src\CurlHttpClient.DependencyInjection" -c Release -o "%REPO%\artifacts" || exit /b 1

echo === SHA-256 manifest (record these in your deployment inventory) ===
powershell -NoProfile -Command ^
  "Get-FileHash '%NATIVE%','%REPO%\native\cacert.pem' -Algorithm SHA256 | Format-Table Hash, Path -AutoSize"
endlocal
