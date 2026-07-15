# Toolchain and dependency manifest

## Native toolchain

| Item | Value |
| --- | --- |
| Compiler | MSVC v142 — Visual Studio 2019 Build Tools, cl 19.29.30159 (14.29.30133) |
| Windows SDK | 10.0.19041 |
| Generator | CMake ≥ 3.24 + Ninja, presets in `native/CurlBridge/CMakePresets.json` |
| Target | x64, `_WIN32_WINNT=0x0603` (Windows 8.1 / Server 2012 R2 API floor) |
| CRT | **Statically linked** (`/MT`, `CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded`) — no VC++ redistributable needed on the target |
| Minimum supported OS | Windows 8.1 / Windows Server 2012 R2 (x64) — enforced by `build/check-imports.ps1` |

Why v142: it is the newest MSVC toolset whose output is fully supported on
Server 2012 R2; binaries import only Win 8.1-era system DLLs. Do not bump the
toolset without re-running the import gate and a real WS2012R2 smoke test.

## Native dependencies (vcpkg, baseline `cd61e1e26a038e82d6550a3ebbe0fbbfe7da78e3` = tag 2026.06.24)

| Package | Version | Role | License |
| --- | --- | --- | --- |
| curl | **8.21.0** | HTTP engine (features: `openssl`, `http2`, `brotli`, `sspi`; `default-features: false`) | curl (MIT-like) |
| openssl | **3.6.3** | TLS 1.2/1.3 | Apache-2.0 |
| nghttp2 | **1.69.0** | HTTP/2 framing | MIT |
| zlib | **1.3.2** | gzip/deflate decoding (unconditional curl dependency) | Zlib |
| brotli | **1.2.0** | br decoding | MIT |

⚠️ `default-features: false` on curl is load-bearing: the port's default
`ssl` feature selects **Schannel** on Windows. The `http2` feature still drags
Schannel in as an *alternate* backend (MultiSSL build); the bridge pins the
process to OpenSSL with `curl_global_sslset(CURLSSLBACKEND_OPENSSL)` before
any libcurl call, and the handler refuses to start unless the active backend
reports as OpenSSL. `sspi` provides NTLM/Negotiate *authentication* (proxies)
only — it does not affect the TLS backend.

## Deployed native artifacts

```
runtimes/win-x64/native/
  curl_http_bridge.dll     (~4.9 MB — everything above statically linked)
  cacert.pem               Mozilla CA bundle, vendored 2026-05-14
```

The DLL's only imports: `KERNEL32`, `ADVAPI32`, `USER32`, `WS2_32`,
`IPHLPAPI`, `CRYPT32`, `BCRYPT`, `Secur32` — all present on Server 2012 R2.

cacert.pem SHA-256 (as vendored):
`86a1f3366afac7c6f8ae9f3c779ac221129328c43f0ab2b8817eb2f362a5025c`

`build/package.cmd` prints the current SHA-256 of both native assets at pack
time — record them in your deployment inventory.

## Managed

| Item | Value |
| --- | --- |
| Target framework | net10.0 (single-target; see docs/limitations.md for the WS2012R2 runtime-support caveat) |
| Interop | `LibraryImport` source-generated P/Invoke, `[UnmanagedCallersOnly]` callbacks, `SafeHandle` |
| Dependencies | `Microsoft.Extensions.Logging.Abstractions` (core); `Microsoft.Extensions.Http` (DI package only) |

## Static linking: licensing and maintenance notes

- All licenses above are permissive; ship the attribution texts with the
  product. OpenSSL 3.x is Apache-2.0 (no advertising clause).
- **You own the CVE cadence.** A statically linked OpenSSL/curl is not
  patched by Windows Update. Subscribe to curl and OpenSSL security
  announcements; a fix means: bump the vcpkg baseline (or add a port
  overlay), rebuild via `build/build-native-x64.cmd`, re-run
  `build/run-tests.cmd`, redeploy. Runtime versions are exposed via
  `GetDiagnostics()` and the `CurlHttpClient` EventSource so deployed
  versions are auditable in production.
