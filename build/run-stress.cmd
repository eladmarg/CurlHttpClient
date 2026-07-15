@echo off
setlocal
rem Runs only the gated stress/soak/memory suites.
rem   Usage: run-stress.cmd [stressMB] [soakSeconds]
set REPO=%~dp0..
set CURLHTTP_STRESS=1
if not "%~1"=="" set CURLHTTP_STRESS_MB=%~1
if not "%~2"=="" set CURLHTTP_SOAK_SECONDS=%~2

dotnet test "%REPO%\tests\CurlHttpClient.IntegrationTests" -c Release --nologo ^
    --filter "FullyQualifiedName~Stress|FullyQualifiedName~CipherMatrixOrchestrator" || exit /b 1
endlocal
