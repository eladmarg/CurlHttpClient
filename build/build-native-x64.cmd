@echo off
setlocal
rem ============================================================
rem Builds curl_http_bridge.dll (x64, MSVC v142, static CRT,
rem statically linked libcurl/OpenSSL/nghttp2/zlib/brotli).
rem Output: native\CurlBridge\out\build\x64-windows-static-v142\
rem ============================================================
set REPO=%~dp0..
set VCPKG_ROOT=%REPO%\tools\vcpkg

if not exist "%VCPKG_ROOT%\vcpkg.exe" (
    echo [1/4] Bootstrapping vcpkg...
    git clone https://github.com/microsoft/vcpkg.git "%VCPKG_ROOT%" || exit /b 1
    pushd "%VCPKG_ROOT%"
    rem Pin to the tag matching builtin-baseline in native\vcpkg.json.
    git checkout 2026.06.24 || exit /b 1
    call bootstrap-vcpkg.bat -disableMetrics || exit /b 1
    popd
) else (
    echo [1/4] vcpkg present.
)

echo [2/4] Locating MSVC v142 (VS 2019 Build Tools)...
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || (
    echo ERROR: VS 2019 Build Tools with the C++ workload are required ^(v142 = WS2012R2-compatible^).
    exit /b 1
)

echo [3/4] Configure + build (dependencies build on first run; ~5 min)...
pushd "%REPO%\native\CurlBridge"
cmake --preset x64-windows-static-v142 || exit /b 1
cmake --build --preset x64-windows-static-v142 || exit /b 1
popd

echo [4/4] Windows Server 2012 R2 import gate + smoke test...
powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO%\build\check-imports.ps1" ^
    -DllPath "%REPO%\native\CurlBridge\out\build\x64-windows-static-v142\curl_http_bridge.dll" || exit /b 1
"%REPO%\native\CurlBridge\out\build\x64-windows-static-v142\curl_bridge_spike.exe" || exit /b 1

echo.
echo Native build OK.
endlocal
