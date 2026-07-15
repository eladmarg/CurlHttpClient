# Architecture

```
Application code
    ↓
System.Net.Http.HttpClient
    ↓
CurlHttpMessageHandler            (option mapping, header translation, exception mapping)
    ↓
CurlRequestDispatcher             (bounded dedicated-thread pool, per-origin limiter)
    ↓
CurlRequestContext                (per-request hub: queues, parser, cancellation)
    ↓  LibraryImport / [UnmanagedCallersOnly] function pointers
curl_http_bridge.dll              (stable C ABI, exception firewall)
    ↓
libcurl 8.x (easy interface)  →  OpenSSL 3.x, nghttp2, zlib, brotli (all static)
```

## Execution model ("Option A": bounded worker pool)

Each request is executed by one **blocking `curl_easy_perform`** on one of a
bounded set of dedicated background threads (`CurlHttp-Worker-N`, never
ThreadPool threads). Blocking is deliberate — it is what provides natural
upload/download backpressure through libcurl — and it is bounded:

- Admission is gated by a semaphore of `MaxConcurrentRequests` slots
  (default `max(8, 2×cores)`).
- A request **owns its worker thread until the response body is fully
  consumed or disposed** — a streaming response pins a thread. Size the pool
  to the number of concurrently open response streams; beyond capacity,
  admission fails fast after `WorkerAdmissionTimeout` with a clear
  `HttpRequestException` instead of silent queueing.
- `MaxConnectionsPerServer` is enforced in managed code with per-origin
  semaphores (libcurl's `CURLOPT_MAXCONNECTS` is an idle-cache size, not a
  concurrency cap).

The C ABI was shaped so a `curl_multi` event-loop backend (single native
thread, socket-driven, true connection sharing and HTTP/2 multiplexing) can
replace the worker pool later without changing `CurlHttpMessageHandler`'s
public surface.

## Connection reuse

libcurl **does not support sharing live connections between concurrent
threads** (that requires the multi interface). Reuse therefore comes from:

1. **Pooled easy handles.** The native client keeps a free-list of easy
   handles. `curl_easy_reset` preserves a handle's live connections, DNS
   cache and TLS-session state, so a handle checked out for request N reuses
   the keep-alive socket it opened for request N−k. Sequential requests to
   one origin therefore reuse one connection (verified by integration test).
2. **A share handle** carrying `CURL_LOCK_DATA_DNS` and
   `CURL_LOCK_DATA_SSL_SESSION` (thread-safe with mutex lock callbacks),
   so DNS results and TLS session tickets are shared across all handles.

Connection lifetime: `CURLOPT_MAXAGE_CONN` ← `PooledConnectionIdleTimeout`,
`CURLOPT_MAXLIFETIME_CONN` ← `PooledConnectionLifetime` (default 15 min —
bounds DNS-failover blindness).

Total idle sockets ≈ pool size × per-handle connection cache; each in-flight
request to one origin may open its own connection (no cross-thread sharing).

## Response streaming and backpressure

```
worker thread (native)                          consumer (any thread)
curl write callback
  → [UnmanagedCallersOnly] trampoline
    → BoundedByteQueue.Write (BLOCKING when full) ─┐
                                                   │ bounded by MaxResponseBufferBytes
CurlResponseStream.ReadAsync  ←────────────────────┘ (TCS-based async waits)
```

The queue is the deadlock-critical piece. Its invariants:

- The producer's blocking wait is **always wakeable** by `Abort()` — a
  consumer that disposes the stream can unblock a producer stuck inside the
  native write callback (libcurl's progress callback cannot fire while
  control is inside our callback, so waiting for it would deadlock).
- Callbacks report aborts **directly to libcurl** via their return values
  (`CURL_WRITEFUNC_ERROR` / `CURL_READFUNC_ABORT`); the xferinfo (progress)
  callback returning non-zero is only the fallback for network-idle phases
  (~1 s latency, requires the threaded resolver — asserted at startup).
- Chunks live in `ArrayPool` buffers, returned as soon as they are copied out.
- On failure, buffered data remains readable and the error surfaces at the
  point the consumer reaches it (matching socket-based handlers).

## Header parsing and response finality

libcurl forwards every header block of a transfer through one callback: 1xx
blocks, every redirect hop, trailers (proxy CONNECT responses are suppressed
natively via `CURLOPT_SUPPRESS_CONNECT_HEADERS`). The native bridge tags each
line (status-line / block-end / 1xx / trailer + block status); the managed
`ResponseHeaderParser` decides **when a block is the final response**:

- 1xx → discarded; trailers → `HttpResponseMessage.TrailingHeaders`.
- 2xx/4xx/5xx, or 3xx without Location / with redirects disabled →
  **immediately final** at the blank line (this is what makes
  `ResponseHeadersRead` return before the body).
- 3xx with Location while redirects are enabled → **ambiguous** (libcurl may
  follow it); the block is parked and promoted to final by the first body
  byte or transfer completion. Safe because libcurl never delivers body bytes
  for a response it acts on.

Headers are decoded as Latin-1, added with `TryAddWithoutValidation`
(response headers first, content headers on rejection), repeated headers stay
separate (`Set-Cookie` never joined). When libcurl transparently decoded the
body (`Content-Encoding` ∈ gzip/deflate/br), `Content-Encoding` +
`Content-Length` are stripped, matching `HttpClientHandler`.

## Request bodies

The content stream is opened on the SendAsync path (faults surface as clean
managed exceptions before native work starts). The native read callback pulls
via `ReadAsync(...).GetAwaiter().GetResult()` on the dedicated worker thread —
correct for async-only streams (e.g. ASP.NET request bodies) and wakeable via
a per-request CTS. Known length → `CURLOPT_INFILESIZE_LARGE`/
`POSTFIELDSIZE_LARGE`; unknown → `Transfer-Encoding: chunked`. Seekable
streams support libcurl rewinds (auth retries/redirect resubmits); non-seekable
streams report `CURL_SEEKFUNC_CANTSEEK`.

Method plumbing: POST uses `CURLOPT_POST` (preserving libcurl's .NET-matching
POST→GET rewrite on 301/302/303); PUT uses the upload path; other verbs with
bodies use upload + `CURLOPT_CUSTOMREQUEST` (method then pinned across
redirects — documented divergence).

## Cancellation and timeouts

Three distinct signals, mapped by `CurlErrorMapper`:

| Signal | Mechanism | Exception |
| --- | --- | --- |
| Caller token | cancel flag + wake queues + native cancel; callbacks abort immediately, xferinfo within ~1 s | `TaskCanceledException` carrying the caller token |
| `ConnectTimeout` | `CURLOPT_CONNECTTIMEOUT_MS`; disambiguated from total timeout by `CURLINFO_CONNECT_TIME_T == 0` | `HttpRequestException(ConnectionError)` + `TimeoutException` inner |
| `RequestTimeout` | `CURLOPT_TIMEOUT_MS`, set **only when configured** (default null: `HttpClient.Timeout` owns the total budget via the token) | `TaskCanceledException` + `TimeoutException` inner |

TLS/cert → `HttpRequestException(SecureConnectionError)` with
`AuthenticationException` inner; DNS → `NameResolutionError`; native detail
(curl code + error buffer) preserved in the message and `Exception.Data`.

## Native ownership and callback lifetime

- Callbacks are **static `[UnmanagedCallersOnly]` function pointers** — there
  is no delegate to keep alive or pin. Per-request state travels as a
  `GCHandle` (allocated before send, freed after the blocking send returns,
  at which point libcurl can no longer call back).
- All native handles are `SafeHandle`s; the SafeHandle ref-count makes the
  cross-thread `curl_bridge_request_cancel` race-free against teardown.
- Every export is wrapped in `try/catch(...)` — no C++ exception crosses the
  ABI; failures become stable result codes + a per-request error buffer.
- `curl_global_init` runs once per process (`std::call_once`), preceded by
  `curl_global_sslset(OpenSSL)`; `curl_global_cleanup` is deliberately never
  called and the DLL is never unloaded (process-lifetime, the only safe
  policy with a static OpenSSL).
- Handler disposal: stop admission → cancel in-flight contexts → join workers
  (≤10 s) → destroy the native client (which itself refuses to free while any
  request is active).

## Diagnostics

`GetDiagnostics()` parses the bridge's version JSON: bridge/curl/OpenSSL
versions, feature flags, protocols, trust source, native library path —
`TlsBackendIsOpenSsl` is the health-check proof that Schannel is not in use.
`CurlHttpClient` EventSource emits request start/stop/failed with duration,
status, HTTP version and connection-reuse flag; optional `ILogger` logging
redacts Authorization/Cookie header lines, and native verbose output never
includes bodies (dropped inside the bridge).
