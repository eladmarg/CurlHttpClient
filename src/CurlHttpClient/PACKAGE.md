# CurlHttpClient

A drop-in `HttpMessageHandler` that routes `HttpClient` through a **bundled,
statically-linked libcurl + OpenSSL** native bridge — so your app gets modern
TLS 1.2/1.3 on hosts whose Schannel cannot, most notably **Windows Server
2012 R2**.

No system OpenSSL, no external native dependencies, no VC++ redistributable:
the bridge DLL is self-contained (static CRT) and ships in the package.

```csharp
using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
{
    // A Mozilla cacert.pem ships in the package and is found automatically,
    // wherever the native bridge landed. Only set CertificateAuthorityBundlePath
    // to use your OWN bundle — and give it an ABSOLUTE path: a relative one is
    // resolved against the current directory, which is C:\Windows\System32 for
    // an IIS worker process.
});
using var client = new HttpClient(handler);

string body = await client.GetStringAsync("https://example.com/");
```

Everything is standard `HttpClient`: `SendAsync`/`Send`, streaming with
`HttpCompletionOption.ResponseHeadersRead`, cancellation, timeouts, redirects,
decompression, proxies, and `HttpClientFactory`.

## Why

`SocketsHttpHandler` uses the OS TLS stack. On Windows Server 2012 R2 that is a
Schannel that cannot negotiate TLS 1.2 with modern cipher suites (and never
TLS 1.3), so calls to up-to-date HTTPS endpoints fail at the handshake. This
handler carries its own TLS stack (OpenSSL 3.x) and cipher support, independent
of the OS.

## Highlights

- **Modern TLS everywhere** — TLS 1.2 and 1.3, OpenSSL cipher suites, certificate
  and hostname verification always on (they cannot be disabled).
- **Self-contained** — one native DLL (libcurl 8.x + OpenSSL 3.x + nghttp2/zlib/
  brotli, all static) plus a `cacert.pem`. Windows x64. Loads only Windows
  8.1-floor system DLLs (build-gated), so it runs on Server 2012 R2.
- **Two engines** — a default dedicated-worker pool, and an opt-in `curl_multi`
  event-loop engine (`ExecutionEngine = CurlExecutionEngine.MultiEventLoop`)
  with HTTP/2 multiplexing, a shared connection pool, and near-instant
  cancellation.
- **Production-hardened** — streaming with backpressure, bounded memory,
  deterministic disposal/cancellation, connection reuse, redacted diagnostics
  (`EventSource` + `ILogger`), and a deep adversarial memory-safety/concurrency
  review.

## Dependency injection

Requires a separate package:

```
dotnet add package CurlHttpClient.DependencyInjection
```

Put the transport under clients you already register — typed clients, Refit
clients, SDK clients:

```csharp
using CurlHttp.DependencyInjection;

services.AddHttpClient<IInvoiceApi, InvoiceApi>()
    .UseCurlHandler(_ => new CurlHttpClientOptions { EnableHttp2 = true });
```

Or register a new named client:

```csharp
services.AddCurlHttpClient("modern-tls", _ => new CurlHttpClientOptions
{
    EnableHttp2 = true,
});
// httpClientFactory.CreateClient("modern-tls")
```

Both set the factory handler lifetime to infinite, which is what this handler
wants — see the package README for why.

## Platform & support notes

- **Windows x64 only.** On other platforms, prefer `SocketsHttpHandler`
  (already OpenSSL-backed off-Windows).
- **.NET 10.** Note that Microsoft does not support the .NET runtime on Server
  2012 R2; the native layer is fully 2012 R2 compatible, and a net8.0 retarget
  is a documented low-risk fallback if the runtime proves unreliable on target.
- Certificate/hostname verification cannot be turned off. Caller-supplied
  cipher strings and request-header values are passed through — validate
  untrusted input yourself.

MIT licensed. See the repository for full documentation, limitations, the
security review, and the deployment checklist.
