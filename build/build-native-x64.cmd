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

echo [2/4] Locating MSVC v142 toolset...
rem Find any VS install (2019 Build Tools locally, or VS2022 with the v142
rem component on CI) that carries the v142 (14.2x) toolset, then activate it
rem specifically. The v142 component id differs between VS2019 and VS2022, so
rem probe for the toolset directory rather than filtering by component id. On
rem VS2022 the default toolset is v143 (not WS2012R2-compatible), hence
rem -vcvars_ver below.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo ERROR: vswhere.exe not found; install Visual Studio or the Build Tools with the C++ workload.
    exit /b 1
)
set "VSINSTALL="
set "V142VER="
for /f "usebackq tokens=* delims=" %%i in (`"%VSWHERE%" -products * -all -property installationPath`) do (
    if not defined V142VER call :find_v142 "%%i"
)
if not defined V142VER (
    echo ERROR: no Visual Studio instance with the v142 ^(14.2x^) C++ toolset was found.
    echo        Install "MSVC v142 - VS 2019 C++ x64/x86 build tools" ^(v142 = WS2012R2-compatible^).
    exit /b 1
)
call "%VSINSTALL%\VC\Auxiliary\Build\vcvarsall.bat" x64 -vcvars_ver=%V142VER% >nul || (
    echo ERROR: failed to initialize the v142 build environment ^(%V142VER%^).
    exit /b 1
)
echo       using "%VSINSTALL%" ^(toolset %V142VER%^)

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
exit /b 0

rem --- subroutine: probe %1 for the v142 (14.2x) toolset, set VSINSTALL/V142VER
:find_v142
set "_v142ver="
for /f "delims=" %%v in ('dir /b /ad "%~1\VC\Tools\MSVC\14.2*" 2^>nul') do set "_v142ver=%%v"
if defined _v142ver set "VSINSTALL=%~1"
if defined _v142ver set "V142VER=%_v142ver%"
goto :eof
