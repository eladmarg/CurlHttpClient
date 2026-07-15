@echo off
setlocal
set REPO=%~dp0..
dotnet build "%REPO%\CurlHttpClient.slnx" -c Release || exit /b 1
endlocal
