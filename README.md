# CurlHttpClient

[![NuGet](https://img.shields.io/nuget/v/CurlHttpClient.svg)](https://www.nuget.org/packages/CurlHttpClient)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A drop-in `HttpMessageHandler` that routes `HttpClient` through a **bundled,
statically-linked libcurl + OpenSSL** native bridge — so your app gets modern
**TLS 1.2 / 1.3** on Windows hosts whose Schannel cannot, most notably
**Windows Server 2012 R2**. No system OpenSSL, no external native dependencies,
no VC++ redistributable. Windows x64.

```csharp
using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions());
using var client  = new HttpClient(handler);

string html = await client.GetStringAsync("https://example.com/");
```

---

## The problem

`HttpClient` (via `SocketsHttpHandler`) uses the **operating system's** TLS
stack. On Windows that is **Schannel**, and on **Windows Server 2012 R2 /
Windows 8.1** Schannel:

- has **no TLS 1.3** at all, and
- negotiates TLS 1.2 only with an **old cipher set** — it lacks the modern
  ECDHE/AES-GCM and ChaCha20-Poly1305 suites that most HTTPS endpoints now
  **require**.

So a perfectly ordinary call from a service still running on 2012 R2:

```csharp
await client.GetAsync("https://api.some-modern-service.com/");
```

fails at the **TLS handshake**, before any HTTP happens:

```
The request was aborted: Could not create SSL/TLS secure channel.
  (or)  An existing connection was forcibly closed by the remote host.
```

You can't fix this from managed code, because the cipher list lives in the OS.
Microsoft's OS-level updates for 2012 R2 have ended, and the endpoints you need
to reach keep tightening their requirements. Upgrading the OS is often not an
option on the timeline you have.

## The solution

CurlHttpClient carries **its own TLS stack** — OpenSSL 3.x, via libcurl —
completely independent of Schannel. It plugs into `HttpClient` as a normal
`HttpMessageHandler`, so **your application code does not change**:

```
        your code
   System.Net.Http.HttpClient
            │
   CurlHttpMessageHandler      ← this package
            │ P/Invoke
   curl_http_bridge.dll        ← bundled, static: libcurl + OpenSSL + nghttp2 + zlib + brotli
            │
        libcurl → OpenSSL      ← modern TLS 1.2 / 1.3, independent of the OS
```

The native bridge is a **single self-contained DLL** (static CRT) that ships in
the NuGet package alongside a Mozilla CA bundle. It imports only Windows
8.1-era system DLLs (enforced by a build gate), so it loads and runs on Server
2012 R2.

## Install

```
dotnet add package CurlHttpClient
```

The package includes the native `curl_http_bridge.dll` and `cacert.pem` under
`runtimes/win-x64/native/`; the .NET SDK copies them to your output
automatically. Windows x64 only.

## Quick start

### Direct

```csharp
using CurlHttp;

using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
{
    // Trust defaults to the bundled Mozilla cacert.pem; override if you want:
    // CertificateAuthorityBundlePath = "/path/to/your/ca-bundle.pem",
    // UseSystemCertificateStore = true,   // also trust the Windows store
    EnableHttp2 = true,                     // optional
});
using var client = new HttpClient(handler);

using var response = await client.GetAsync(
    "https://api.some-modern-service.com/v1/status",
    HttpCompletionOption.ResponseHeadersRead);

response.EnsureSuccessStatusCode();
await using Stream body = await response.Content.ReadAsStreamAsync();
```

Everything is ordinary `HttpClient`: `GetAsync`/`PostAsync`/`SendAsync`, the
synchronous `Send`, streaming with `ResponseHeadersRead`, cancellation tokens,
timeouts, redirects, decompression, and proxies.

### With dependency injection / `IHttpClientFactory`

```
dotnet add package CurlHttpClient.DependencyInjection
```

```csharp
using CurlHttp.DependencyInjection;

builder.Services.AddCurlHttpClient("modern-tls", _ => new CurlHttpClientOptions
{
    EnableHttp2 = true,
});

// elsewhere:
HttpClient client = httpClientFactory.CreateClient("modern-tls");
```

The handler is registered with an **infinite** `HttpClientFactory` lifetime by
design — it is meant to be long-lived (its native connection pools live inside
it), and connection staleness is bounded by `PooledConnectionLifetime`.

## Configuration

`CurlHttpClientOptions` is validated once at handler construction. The most
common knobs:

| Option | Default | Purpose |
| --- | --- | --- |
| `CertificateAuthorityBundlePath` | bundled `cacert.pem` | PEM CA bundle used for verification |
| `UseSystemCertificateStore` | `false` | also trust the Windows certificate store |
| `MinimumTlsVersion` | 1.2 | floor TLS version (1.2 or 1.3) |
| `EnableHttp2` | `false` | negotiate HTTP/2 (multiplexes on the event-loop engine) |
| `AllowAutoRedirect` / `MaxAutomaticRedirections` | `true` / 10 | redirect following |
| `AutomaticDecompression` | on | transparent gzip/deflate/br |
| `ConnectTimeout` / `RequestTimeout` | 30 s / none | timeouts |
| `MaxConnectionsPerServer` | 0 (unlimited) | per-origin concurrency cap |
| `MaxResponseBufferBytes` | 1 MiB | streaming backpressure threshold |
| `MaxResponseHeadersLength` | 1 MiB | response header-block size cap |
| `ExecutionEngine` | `DedicatedWorkers` | see below |

Proxies are configured with a standard `IWebProxy` on the options.

## Execution engines

| Engine | When to use |
| --- | --- |
| **`DedicatedWorkers`** (default) | Best single-request latency and lowest allocation; a bounded pool of blocking threads. No cross-thread connection sharing. |
| **`MultiEventLoop`** (opt-in) | One `curl_multi` loop thread for all transfers: HTTP/2 stream multiplexing, one shared connection pool, and near-instant cancellation. Best for high concurrency to one origin. |

```csharp
new CurlHttpClientOptions { ExecutionEngine = CurlExecutionEngine.MultiEventLoop }
```

## Requirements & platform support

- **Windows x64.** On other platforms use `SocketsHttpHandler` (already
  OpenSSL-backed off-Windows).
- **.NET 10.** Note: Microsoft does **not** support the .NET runtime on Server
  2012 R2 (last supported: .NET 8). The **native** layer is fully 2012 R2
  compatible; if the .NET 10 runtime proves unreliable on target, retargeting
  to `net8.0` is a documented, low-risk change. See
  [docs/deployment-ws2012r2.md](docs/deployment-ws2012r2.md).

## Security

- **Certificate and hostname verification are always on** and **cannot be
  disabled** through the public API. Trust comes from the bundled `cacert.pem`,
  your own CA bundle, and/or the Windows store (opt-in).
- **OpenSSL is pinned** at load time; the handler refuses to start unless
  libcurl reports the OpenSSL backend.
- Caller-supplied cipher strings and request-header values are passed through —
  validate untrusted input yourself. See
  [docs/limitations.md](docs/limitations.md) and
  [docs/security-review.md](docs/security-review.md).

## What's bundled

libcurl 8.21.0, OpenSSL 3.6.3, nghttp2, zlib, and brotli — statically linked
into `curl_http_bridge.dll` with a static CRT — plus a Mozilla `cacert.pem`.
Each third-party component keeps its own license (see [LICENSE](LICENSE)). A
SHA-256 manifest of the native assets is emitted at pack time for your
deployment inventory.

## Quality

- Certified test suite: full `HttpClient` API coverage, an exact-build cipher
  matrix, TLS matrices, and gated stress/soak — run against **both** engines.
- Performance-optimized and regression-gated.
- A deep adversarial memory-safety / concurrency / security review (report in
  [artifacts/review/](artifacts/review/)): no known leaks, use-after-free,
  double-free, or deadlocks; lock ordering proven acyclic.

## Building from source

```cmd
build\build-native-x64.cmd   :: bootstraps vcpkg, builds curl_http_bridge.dll (v142, static CRT)
build\build-managed.cmd
build\run-tests.cmd
dotnet run --project samples\CurlHttpClient.Sample
```

Testing & certification:

```cmd
dotnet test                     :: all suites except gated stress; coverage gate enforcing
build\run-stress.cmd            :: full cipher matrix + stress/soak/memory
build\run-coverage.cmd          :: everything + artifacts/ tree + verification-summary.md
build\validate-ws2012r2.ps1     :: on-target checklist (clean WS2012R2 machine)
```

Set `CURLHTTP_ENGINE=multi` to run the behavioral suite against the event-loop
engine.

## Documentation

| Doc | Contents |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | design, threading model, native ownership, callback lifetimes |
| [docs/deployment-ws2012r2.md](docs/deployment-ws2012r2.md) | deployment + validation checklist for Windows Server 2012 R2 |
| [docs/security-review.md](docs/security-review.md) | TLS posture, CVE ownership, redaction, DLL loading |
| [docs/limitations.md](docs/limitations.md) | known limitations and divergences from SocketsHttpHandler |
| [docs/performance-notes.md](docs/performance-notes.md) | benchmark results and tuning knobs |
| [docs/cipher-validation.md](docs/cipher-validation.md) | per-suite TLS cipher matrix |
| [docs/troubleshooting.md](docs/troubleshooting.md) | common failures and diagnostics |
| [docs/versions.md](docs/versions.md) | toolchain, dependency versions, SHA-256 manifest |
| [CHANGELOG.md](CHANGELOG.md) | release notes |

## Project layout

```
src/CurlHttpClient/                    net10.0 handler + interop
src/CurlHttpClient.DependencyInjection IHttpClientFactory integration
native/CurlBridge/                     C++ bridge (stable C ABI) around libcurl
native/cacert.pem                      vendored Mozilla CA bundle
tests/                                 unit + integration (TLS / parity / stress)
benchmarks/                            BenchmarkDotNet vs SocketsHttpHandler
samples/CurlHttpClient.Sample          runnable demo
```

## License

MIT — see [LICENSE](LICENSE). Bundled third-party components (libcurl, OpenSSL,
nghttp2, zlib, brotli, the Mozilla CA set) are redistributed under their own
licenses, noted in the license file.
