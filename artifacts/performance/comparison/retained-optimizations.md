# Retained optimizations

Each was measured before/after; kept because it improved a metric without
regressing correctness or the other workload profile. Correctness was verified
by the full certification suite (376 tests) after every stage.

## P1 — CA bundle as file path, not blob
`native/CurlBridge/src/bridge_request.cpp`, `CurlHttpMessageHandler.LoadCaBundle`.
`CURLOPT_CAINFO_BLOB` disqualifies OpenSSL's X509-store cache
(`CURLOPT_CA_CACHE_TIMEOUT`, 24 h), so the ~200 KB PEM was re-parsed on every
new TLS connection. Switched to `CURLOPT_CAINFO` (a real path exists on both
managed trust paths). **3.9× faster per new connection** (9,562 → 2,435 µs).
Trust behavior is byte-for-byte identical (TLS + cert-validation matrices green).
Caveat documented: `UseSystemCertificateStore` (`CURLSSLOPT_NATIVE_CA`) also
disqualifies the cache — not fixable, only documented.

## P2 — Managed hot-path fixes
- **P2a Sync stream read** (`CurlResponseStream.Read(Span)`): was `new byte[len]`
  + `Task<int>` per call → Monitor-blocking queue read. **264 KB/op → 1.8 KB/op.**
- **P2b Reused upload buffer + `ValueTask` sync fast path** (`OnReadBody`): one
  per-request pooled buffer instead of rent/return per callback; the sync fast
  path (`IsCompletedSuccessfully ? Result : AsTask()...`) skips a `Task` alloc
  for MemoryStream-backed bodies.
- **P2c EventSource string hygiene**: build redacted-URL strings only when a
  listener is enabled.
- **P2d Lazy `_bodyReadCts`**: allocated only when the request has a body;
  bodyless GETs pay nothing.
- **P2e Span-based header parse**: status line / blank-line handling on spans
  before string materialization (final header strings still required by
  `HttpHeaders`).

## P3 — Rendezvous single-copy streaming
`src/CurlHttpClient/Internal/BoundedByteQueue.cs`. A parked reader registers its
destination buffer; the producer fills it directly under the lock when the
segment queue is empty, eliminating the pooled intermediate copy + rent/return
per chunk in steady-state streaming (~halves copies/byte on downloads).
Invariants (direct-fill only when queue empty; single atomically-registered
pending reader; only the claimant completes the TCS; copy under lock;
`RunContinuationsAsynchronously`) verified by 8 added race tests
(cancel-vs-fill, abort-vs-fill, ordering with queued segments, sync-read
interleave, Complete/Fault with a pending reader). Contributes to the download
throughput gain.

## P4 — Native option quick wins
`UploadBufferSize` option → `CURLOPT_UPLOAD_BUFFERSIZE`; `CURLOPT_PIPEWAIT` when
`EnableHttp2`; copy `CURLINFO_EFFECTIVE_URL` only when it differs from the
request URL. Rebuilt DLL; ABI test + import gate green.

## P6 — Performance regression gates
`tests/.../Stress/PerformanceRegressionTests.cs`: allocated-bytes budgets per
reused GET (async + sync), allocations per response MB, thread-count-at-
concurrency, cancellation-latency generous bound. Gated/CI-friendly, no
wall-clock equality assertions. Locks in the P1–P3 wins against regression.

## P7 — curl_multi event-loop engine (opt-in)
`native/CurlBridge/src/bridge_multi.cpp`, `CurlMultiDispatcher`, multi-mode in
`CurlRequestContext`/`BoundedByteQueue`. One loop thread, one shared connection
pool, pause-based backpressure. **Cancellation 979 ms → 0.26 ms**, **32 MB
download −16% vs workers**, connection consolidation across all requests, no
per-request thread. Kept as opt-in (workers stays default) because it is only
at parity on the single-request latency profile and adds a one-time per-handler
loop-thread startup. Validated against the full suite on both engines.
