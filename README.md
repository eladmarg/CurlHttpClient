# CurlHttpClient

A production-grade `HttpMessageHandler` that routes `HttpClient` traffic through a **bundled, statically linked libcurl + OpenSSL** native bridge instead of the operating system's TLS stack.

Built for one job: giving applications on **Windows Server 2012 R2** (whose Schannel cannot negotiate modern TLS versions and cipher suites) access to TLS 1.2/1.3 endpoints — **without changing application code**:

```csharp
using var client = new HttpClient(new CurlHttpMessageHandler(options));

using var response = await client.SendAsync(
    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
```

## Layout

```
src/CurlHttpClient/                    net10.0 handler + interop
src/CurlHttpClient.DependencyInjection IHttpClientFactory integration
native/CurlBridge/                     C++ bridge (stable C ABI) around libcurl
native/vcpkg.json                      pinned native dependency manifest
native/cacert.pem                      vendored Mozilla CA bundle
tests/CurlHttpClient.Tests             unit tests
tests/CurlHttpClient.IntegrationTests  Kestrel/raw-TCP/TLS/parity tests
benchmarks/                            BenchmarkDotNet vs SocketsHttpHandler
samples/CurlHttpClient.Sample          acceptance-criteria demo
build/                                 build-native-x64.cmd, build-managed.cmd,
                                       run-tests.cmd, package.cmd
docs/                                  architecture, deployment, security, ...
```

## Quick start

```cmd
build\build-native-x64.cmd   :: bootstraps vcpkg, builds curl_http_bridge.dll (v142, static)
build\build-managed.cmd
build\run-tests.cmd
dotnet run --project samples\CurlHttpClient.Sample
```

## Documentation

| Doc | Contents |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | design, threading model, native ownership, callback lifetimes |
| [docs/versions.md](docs/versions.md) | toolchain, dependency versions, SHA-256 manifest |
| [docs/deployment-ws2012r2.md](docs/deployment-ws2012r2.md) | deployment + validation checklist for Windows Server 2012 R2 |
| [docs/security-review.md](docs/security-review.md) | TLS posture, CVE ownership, redaction, DLL loading |
| [docs/troubleshooting.md](docs/troubleshooting.md) | common failures and their diagnostics |
| [docs/performance-notes.md](docs/performance-notes.md) | benchmark results and tuning knobs |
| [docs/cipher-validation.md](docs/cipher-validation.md) | per-suite TLS cipher matrix, validated against pinned openssl s_server instances |
| [docs/limitations.md](docs/limitations.md) | known limitations and divergences from SocketsHttpHandler |

## Key properties

- **TLS is always verified.** Peer and hostname verification cannot be disabled through the public API. Trust comes from the vendored `cacert.pem` (default), a custom CA bundle, and/or the Windows store (opt-in).
- **OpenSSL is pinned.** The bridge calls `curl_global_sslset(OpenSSL)` before any libcurl use and the handler refuses to start unless `curl_version_info` proves the OpenSSL backend; `GetDiagnostics().TlsBackendIsOpenSsl` provides the runtime health-check proof.
- **True streaming.** Response bodies flow through a bounded, wakeable queue (default 1 MiB) — headers return immediately with `ResponseHeadersRead`, backpressure pauses the native transfer, disposal aborts it.
- **One self-contained DLL.** libcurl 8.21.0 + OpenSSL 3.6.3 + nghttp2 + zlib + brotli statically linked into `curl_http_bridge.dll` with a static CRT; imports only Windows 8.1-era system DLLs (build-gated).
