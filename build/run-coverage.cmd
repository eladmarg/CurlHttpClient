@echo off
setlocal
rem ============================================================
rem Full certification run: native ABI tests + native dependency
rem report + all managed suites (stress gated on) with enforcing
rem API-coverage gate, emitting the artifacts/ tree and the final
rem verification summary.
rem   Usage: run-coverage.cmd [stressMB] [soakSeconds]
rem ============================================================
set REPO=%~dp0..
set NATIVE=%REPO%\native\CurlBridge\out\build\x64-windows-static-v142
set ARTIFACTS=%REPO%\artifacts

set CURLHTTP_ENFORCE_COVERAGE=1
set CURLHTTP_STRESS=1
if not "%~1"=="" set CURLHTTP_STRESS_MB=%~1
if not "%~2"=="" set CURLHTTP_SOAK_SECONDS=%~2

echo [1/6] Native ABI contract tests...
"%NATIVE%\curl_bridge_abi_test.exe" || exit /b 1

echo [2/6] Windows Server 2012 R2 import gate...
powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO%\build\check-imports.ps1" ^
    -DllPath "%NATIVE%\curl_http_bridge.dll" || exit /b 1

echo [3/6] Native dependency report...
powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO%\build\native-dependency-report.ps1" ^
    -DllPath "%NATIVE%\curl_http_bridge.dll" -OutFile "%ARTIFACTS%\compatibility\native-dependencies.json" || exit /b 1

echo [4/6] Unit tests...
dotnet test "%REPO%\tests\CurlHttpClient.Tests" -c Release --nologo ^
    --logger "trx;LogFileName=unit-tests.trx" ^
    --results-directory "%ARTIFACTS%\test-results" || exit /b 1

echo [5/6] Integration + TLS + cipher + stress suites (several minutes)...
dotnet test "%REPO%\tests\CurlHttpClient.IntegrationTests" -c Release --nologo ^
    --logger "trx;LogFileName=integration-tests.trx" ^
    --results-directory "%ARTIFACTS%\test-results" || exit /b 1

echo [6/6] Final certification summary...
dotnet run --project "%REPO%\tools\CurlHttpClient.Reports" -c Release -- "%ARTIFACTS%" || exit /b 1

echo.
echo Coverage run complete. See %ARTIFACTS%\final\verification-summary.md
endlocal
