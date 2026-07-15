# Test strategy

## Objective

Demonstrate, with deterministic evidence, that `CurlHttpMessageHandler`
behaves as a correct `HttpClient` transport while routing every request
through the bundled libcurl + OpenSSL native bridge — and that it does so on
the packaged binaries, not on a developer's machine assumptions.

## Principles

- **Deterministic & local.** Every authoritative test runs against an
  in-process server (Kestrel, raw `TcpListener`, `SslStream`, or a pinned
  `openssl s_server`). Public internet is used only by explicitly-tagged
  smoke tests (`Category=RequiresInternet`), never for pass/fail of a
  capability.
- **Server-side evidence.** Connection reuse is proven by server-stamped
  connection ids; negotiated ciphers/protocols are read back from the
  server, not inferred. Upload streaming is proven by the server observing
  the first byte long before the slow producer finishes.
- **Exact-build truth.** The cipher inventory is enumerated by OpenSSL's own
  API *inside the shipped DLL* (`curl_bridge_enumerate_ciphers`), so the
  matrix can never describe a different build than the one deployed.
- **No silent skips.** Gated suites report **skipped** (never green-with-no-
  work) via `SkippableFact`. The cipher orchestrator asserts one classified
  row per discovered suite. The API-coverage gate fails on any new/untested
  public API.
- **Strict about leaks & deadlocks.** Churn loops assert bounded managed
  memory and OS handle counts; streaming asserts bounded buffering;
  cancellation asserts prompt completion including the backpressure-blocked
  path; the sync-Send starvation scenario asserts no deadlock.

## Test layers

| Layer | Project / folder | What it proves |
| --- | --- | --- |
| Managed unit | `tests/CurlHttpClient.Tests` | header parsing, error mapping, options, bounded queue, cipher-manifest classification |
| Native ABI | `native/CurlBridge/…/abi_test_main.cpp` | C ABI contracts: NULL-handle safety, buffer contracts, proxy refusal, cancel idempotence |
| API coverage | `IntegrationTests/ApiCoverage` | reflection inventory of all 100 HttpClient/JSON APIs + enforcing gate |
| Methods / content / headers / status / redirects / compression | `IntegrationTests/{Methods,Content,Headers,StatusCodes,Redirects,Compression}` | HTTP semantics vs documented policy; byte-exact bodies |
| Failure modes | `IntegrationTests/{Proxy,Cancellation,Timeouts,Errors,Disposal}` | proxy matrix, cancellation phases, timeout distinguishability, error mapping, disposal races |
| Pooling | `IntegrationTests/Pooling` | reuse, eviction, cookie-scrub, per-origin limit (server-side ids) |
| TLS | `IntegrationTests/{CipherSuites,Tls}` | manifest cipher matrix, protocol/group/sigalg/SNI/ALPN/mTLS/cert matrices, resumption |
| Stress (gated) | `IntegrationTests/Stress` | concurrency ladder, soak, memory/handle stability, bounded buffer, sync-Send starvation |
| Consumer | `consumer/` | the packed NuGet package works via plain HttpClient (real deployment shape) |
| Differential | `IntegrationTests/SocketsHandlerParityTests` | equivalence to SocketsHttpHandler where expected; divergences documented |

## Execution

| Command | Scope | Time |
| --- | --- | --- |
| `dotnet test` | everything except gated stress (stress reports skipped) | ~1 min |
| `build\run-stress.cmd` | full cipher matrix + stress/soak/memory | ~8 min |
| `build\run-coverage.cmd` | native ABI + all suites (stress on) + artifacts + summary | ~10 min |
| `build\validate-ws2012r2.ps1` | on-target validation (clean WS2012R2 VM) | manual |

## Gates (CI fails when)

- A new public HttpClient API appears without baseline classification, or a
  baseline API vanishes (SDK drift).
- A Direct-classified API has no `[ApiCoverage]` test (`CURLHTTP_ENFORCE_COVERAGE=1`).
- The runtime TLS backend is not OpenSSL.
- Any discovered cipher fails its forced negotiation, or the cipher count
  drifts from the packaged OpenSSL.
- A native import outside the Windows 8.1 allowlist appears.
- A resource-stability threshold is exceeded.
