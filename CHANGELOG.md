# Changelog

All notable changes to CurlHttpClient are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## 1.1.0

Fixes for two defects found while adopting 1.0.0 in a production ASP.NET Core
application carrying all external HTTPS (payment gateways, transactional email,
AI APIs, Google/Microsoft SDKs). Both were found by differential testing against
`SocketsHttpHandler` over a real TLS server.

### Fixed

- **A retried request sent an empty body.** The request body was read through
  `HttpContent.ReadAsStreamAsync()`, which caches its stream, so any handler
  above this one that re-sent the request (`AddStandardResilienceHandler`, the
  Google.Apis backoff handler, the Microsoft.Graph retry middleware) got a
  drained stream and transmitted 0 bytes against a correct `Content-Length` —
  failing with libcurl error 26. Seekable bodies (the buffered content types:
  `StringContent`, `ByteArrayContent`, `JsonContent`, `FormUrlEncodedContent`,
  `MultipartFormDataContent`) are now rewound to the content's own origin and
  resent in full on every attempt. A genuinely one-shot, non-seekable body still
  cannot be replayed — correct — but now fails with an `HttpRequestException`
  naming the rewind as the cause instead of a raw libcurl read error. An attempt
  that failed before reading any of the body (DNS, connect, TLS) leaves it
  replayable.
- **Multi-valued request headers were corrupted.** Headers were written as one
  wire line per stored value, so `UserAgent.ParseAdd("Product/1.0 (comment)")` —
  which .NET stores as two values — arrived as two `User-Agent` lines and was
  rejoined by the server as `Product/1.0,(comment)`. Each header is now written
  as a single line using the separator .NET itself defines for it (`" "` for
  `User-Agent`, `"; "` for `Cookie`, `", "` for genuine list headers), matching
  `SocketsHttpHandler` byte-for-byte. Affects `User-Agent` (Have I Been Pwned
  rate-limits callers without a valid one; the Google API client sends
  `google-api-dotnet-client/<version> (gzip)`), `Cookie`, and multi-valued
  content headers such as `Content-Language`.

### Added

- `UseCurlHandler(this IHttpClientBuilder, ...)` in
  `CurlHttpClient.DependencyInjection` — attaches the transport to an
  `HttpClient` that is already registered (a typed client, a Refit client, an
  SDK's client), which is what most adopters need. `AddCurlHttpClient` continues
  to register a new named client and is now implemented in terms of it. Both set
  the factory handler lifetime to infinite.
- Test-server endpoint `/echo-headers-raw` returning unjoined header lines.
  `/echo-headers` collapses repeated lines via `StringValues.ToString()`, which
  is why the header defect above was invisible to the existing suite.

### Documentation

- `CertificateAuthorityBundlePath` no longer shows a **relative** example path.
  The bundled `cacert.pem` is found automatically in either deployment layout;
  a relative override resolves against the process working directory, which is
  `C:\Windows\System32` for an IIS worker — so the documented snippet failed on
  exactly the servers this package targets. Absolute paths are now called for.
- `NativeLibraryPath` and the deployment docs now state that **both** layouts are
  probed: `runtimes\win-x64\native\` (RID-agnostic build) and the application
  root (RID-specific `dotnet publish -r win-x64`, which flattens native assets).
  The resolver always supported both; only the documentation understated it.
- The NuGet-facing README now states that the DI snippet needs the separate
  `CurlHttpClient.DependencyInjection` package.
- `docs/limitations.md` documents replay behaviour across a retry.

## 1.0.0 — initial public release

First public release: a self-contained `HttpMessageHandler` that gives
`HttpClient` modern TLS 1.2/1.3 via a bundled, statically-linked libcurl +
OpenSSL native bridge, for Windows x64 hosts (notably Windows Server 2012 R2)
whose Schannel cannot negotiate modern TLS.

### Features

- Full `HttpClient` surface: async `SendAsync` and synchronous `Send`,
  streaming (`ResponseHeadersRead`) with backpressure and bounded memory,
  cancellation, timeouts, redirects, automatic decompression, and explicit
  proxy support.
- TLS 1.2 and 1.3 with OpenSSL cipher suites; certificate and hostname
  verification always enforced. Bundled Mozilla `cacert.pem`, or supply your
  own CA bundle / opt into the OS trust store.
- Two execution engines: a default dedicated-worker pool, and an opt-in
  `curl_multi` event-loop engine (`ExecutionEngine = CurlExecutionEngine.MultiEventLoop`)
  with HTTP/2 multiplexing, a shared connection pool, and near-instant
  cancellation.
- Connection pooling and keep-alive reuse; redacted diagnostics via
  `EventSource` and `ILogger`; `IHttpClientFactory` integration in the
  companion `CurlHttpClient.DependencyInjection` package.
- Self-contained native DLL (static CRT, Windows 8.1-floor imports — build
  gated) plus a SHA-256 asset manifest; no VC++ redistributable required.

### Engineering

- Certified test suite: full `HttpClient` API coverage, exact-build cipher
  matrix, TLS matrices, and gated stress/soak — run against both engines.
- Performance-optimized (measured, regression-gated): e.g. sync `Send`
  allocation 264 KB/op → 1.8 KB/op, 3.9× faster new TLS connections, and
  near-instant cancellation on the event-loop engine.
- Deep adversarial memory-safety / concurrency / security review: fixed
  latent races and shutdown/cancellation hazards, hardened the native C ABI
  against exceptions, added lifetime/race regression tests, and produced a
  full review report (`artifacts/review/`). No known leaks, use-after-free,
  double-free, or deadlocks; lock ordering proven acyclic.

### Notes for consumers

- **Windows x64 only.** Prefer `SocketsHttpHandler` on other platforms.
- `CurlHttpClientOptions.Validate()` runs at handler construction and throws
  `ArgumentException` for out-of-range configuration (e.g. a timeout beyond
  libcurl's ~24.8-day range, a negative `MaxConnectionsPerServer`, or an
  `UploadBufferSize` above 2 MiB).
- `MaxResponseHeadersLength` (default 1 MiB) bounds the response header block;
  a server exceeding it fails the transfer.
- Certificate/hostname verification cannot be disabled. Caller-supplied cipher
  strings and request-header values are passed through — validate untrusted
  input. See `docs/limitations.md`.
- Running the .NET 10 runtime on Windows Server 2012 R2 is outside Microsoft's
  support matrix; the native layer is fully compatible and a net8.0 retarget is
  a documented fallback. See `docs/deployment-ws2012r2.md`.
