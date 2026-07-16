# Changelog

All notable changes to CurlHttpClient are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

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
