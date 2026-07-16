cmake_minimum_required(VERSION 3.24)

project(curl_http_bridge LANGUAGES CXX VERSION 1.0.0)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Static CRT: the DLL must not require a VC++ redistributable on the
# deployment machine (Windows Server 2012 R2).
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

find_package(CURL CONFIG REQUIRED)

find_package(OpenSSL REQUIRED)

add_library(curl_http_bridge SHARED
    src/bridge_global.cpp
    src/bridge_client.cpp
    src/bridge_request.cpp
    src/bridge_callbacks.cpp
    src/bridge_error.cpp
    src/bridge_ciphers.cpp
    src/bridge_multi.cpp
)

target_include_directories(curl_http_bridge
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(curl_http_bridge PRIVATE
    CURL_BRIDGE_EXPORTS
    CURL_STATICLIB
    # Windows Server 2012 R2 / Windows 8.1 API floor.
    _WIN32_WINNT=0x0603
    WINVER=0x0603
    NOMINMAX
    WIN32_LEAN_AND_MEAN
)

if(MSVC)
    # /WX: the bridge sources are warning-clean at /W4; keep them that way.
    # vcpkg headers are compiled /external:W0 so they do not trip it.
    target_compile_options(curl_http_bridge PRIVATE /W4 /WX /permissive- /EHsc /guard:cf)
    target_link_options(curl_http_bridge PRIVATE /guard:cf)

    # Opt-in MSVC static analysis (CI/diagnostics only): cmake --preset ...
    # -DCURL_BRIDGE_ANALYZE=ON. /analyze:external- skips the vcpkg headers.
    option(CURL_BRIDGE_ANALYZE "Enable MSVC /analyze static analysis" OFF)
    if(CURL_BRIDGE_ANALYZE)
        target_compile_options(curl_http_bridge PRIVATE /analyze /analyze:external-)
    endif()

    # Opt-in AddressSanitizer for the diagnostics harness (soak/abi exes).
    # Diagnostics-only and best built with a current toolset (v143); the
    # shipped DLL stays on v142. Incompatible with /guard:cf, so drop it here.
    option(CURL_BRIDGE_ASAN "Build the native test harnesses with ASan" OFF)
endif()

# OpenSSL is the same static archive libcurl links; the direct dependency is
# for the cipher-inventory export (bridge_ciphers.cpp).
target_link_libraries(curl_http_bridge PRIVATE CURL::libcurl OpenSSL::SSL OpenSSL::Crypto)

# Stage-0 spike: asserts the linked libcurl has the OpenSSL backend and the
# features this project depends on. Run on the build machine and as part of
# the WS2012R2 validation checklist.
add_executable(curl_bridge_spike src/spike_main.cpp)
target_link_libraries(curl_bridge_spike PRIVATE curl_http_bridge)
target_include_directories(curl_bridge_spike PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
if(MSVC)
    target_compile_options(curl_bridge_spike PRIVATE /W4 /EHsc)
endif()

# Native ABI contract tests (run by build scripts / CI; exit 0 = pass).
add_executable(curl_bridge_abi_test src/abi_test_main.cpp)
target_link_libraries(curl_bridge_abi_test PRIVATE curl_http_bridge)
target_include_directories(curl_bridge_abi_test PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
if(MSVC)
    target_compile_options(curl_bridge_abi_test PRIVATE /W4 /EHsc)
endif()
