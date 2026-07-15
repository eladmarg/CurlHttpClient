@echo off
setlocal
set REPO=%~dp0..
echo === Unit tests ===
dotnet test "%REPO%\tests\CurlHttpClient.Tests" -c Release --nologo || exit /b 1
echo === Integration + TLS + parity tests (real native bridge) ===
dotnet test "%REPO%\tests\CurlHttpClient.IntegrationTests" -c Release --nologo || exit /b 1
endlocal
