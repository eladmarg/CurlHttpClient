# Known limitations and divergences

## Platform / runtime

1. **.NET 10 on Windows Server 2012 R2 is not supported by Microsoft** (last
   supported: .NET 8). Accepted project risk; go/no-go validation and the
   net8.0 retarget fallback are described in docs/deployment-ws2012r2.md.
   The native layer is fully 2012 R2 compatible.
2. Windows x64 only. Other platforms should use `SocketsHttpHandler`
   (already OpenSSL-backed off-Windows).
3. One native bridge per process: the first handler's `NativeLibraryPath`
   wins; a conflicting later path throws.

## Architecture — default engine (`DedicatedWorkers`, blocking worker pool)

4. **No HTTP/2 multiplexing and no cross-thread connection sharing** —
   libcurl only multiplexes/shares within one easy/multi handle, and each
   pooled worker owns its own easy handle. N concurrent requests to one origin
   = up to N connections. HTTP/2 (`EnableHttp2`) on this engine is therefore
   purely a protocol-compatibility switch. The `MultiEventLoop` engine (below)
   lifts this.
5. Each in-flight request (including an open streaming response) pins one
   dedicated worker thread; admission beyond `MaxConcurrentRequests` fails
   fast after `WorkerAdmissionTimeout`.
6. **Cancellation latency floor ~1 s**: an in-flight transfer waiting on the
   network (e.g. blocked reading response headers) is aborted at libcurl's
   progress-callback cadence (~1 s), not instantly. The `MultiEventLoop`
   engine cancels in well under a millisecond (`curl_multi_wakeup`).

## Architecture — opt-in engine (`ExecutionEngine = MultiEventLoop`)

A single dedicated loop thread drives all transfers through one `curl_multi`
handle. Connections live in one shared pool (consolidated across every
request), HTTP/2 streams multiplex onto one connection, and cancellation is
near-instant. Correctness is identical to the default engine — the entire
certification suite passes against both. Choose it for high concurrency to a
single origin (especially with `EnableHttp2`), cancellation-sensitive
workloads, or many idle-but-reused connections. Its caveats:

  - **HTTP/2 upstream pause buffering**: pausing the write side
    (`CURL_WRITEFUNC_PAUSE`) stops *our* draining, but nghttp2 may already have
    buffered up to the HTTP/2 stream/connection flow-control window (order
    ~10 MB) before the pause takes effect. Peak resident memory for a stalled
    HTTP/2 download can therefore exceed `MaxResponseBufferBytes` by roughly
    one stream window. HTTP/1.1 backpressure is exact (TCP-level). Bound this
    by consuming response streams promptly on HTTP/2.
  - **Admission timeout counts while queued**: `WorkerAdmissionTimeout` bounds
    the wait for an in-flight slot / per-origin permit, and that clock runs
    while the request is queued behind the concurrency gate — a request can
    time out at admission before ever reaching the wire under sustained
    saturation. Size `MaxConcurrentRequests` / `MaxConnectionsPerServer` for
    the offered load.
  - **Per-handler startup cost**: creating a handler on this engine spins up
    the loop thread (~300 µs one-time), versus the worker engine's lazy
    threads. Immaterial for a long-lived, shared handler (the intended usage);
    avoid churning handlers per request on either engine.

Synchronous `HttpClient.Send` is supported on both engines (the async transfer
is buffered on the calling thread).

## Behavioral divergences from SocketsHttpHandler

7. **Redirect method rewriting**: POST→GET on 301/302/303 matches .NET
   (tested). But PATCH/DELETE/custom verbs with bodies use
   `CURLOPT_CUSTOMREQUEST`, which pins the method verbatim across redirects
   instead of .NET's rewrite rules. Redirected non-POST bodied requests are
   rare; avoid relying on rewrite semantics for them.
8. **Exceeding `MaxAutomaticRedirections`** throws `HttpRequestException`
   (libcurl `CURLE_TOO_MANY_REDIRECTS`), whereas SocketsHttpHandler returns
   the last 3xx response.
9. **Cookies**: no `CookieContainer`. `Set-Cookie` is always surfaced
   verbatim; the opt-in `EnableCookieEngineForRedirects` replays cookies
   within one redirect chain only (scrubbed between requests). Apps needing a
   persistent cookie jar must manage headers themselves.
10. **Proxy**: only explicit `IWebProxy` configurations are honored — no
    WinINET/WPAD system-proxy detection and (by design) no
    `HTTP_PROXY`-style environment variables. Proxy auth is
    username/password (`CURLAUTH_ANY`, SSPI available for NTLM/Negotiate);
    no interactive credential flows.
11. **`HttpRequestMessage.Version` is ignored**; the negotiated version is
    handler-level (`EnableHttp2`) and reported on the response.
12. **1xx interim responses** are consumed and discarded (as with
    HttpClient; `Expect: 100-continue` is suppressed unless
    `request.Headers.ExpectContinue == true`).
13. Non-seekable request bodies cannot be replayed if a server demands a
    rewind mid-transfer (libcurl gets `CURL_SEEKFUNC_CANTSEEK` and may fail
    the transfer) — same constraint as SocketsHttpHandler.
14. `response.RequestMessage.RequestUri` is updated to the final redirect
    target; internally-tracked hop URIs come from Location-header resolution
    and are confirmed against `CURLINFO_EFFECTIVE_URL` at transfer end.

## TLS configuration edge cases

16. **`MinimumTlsVersion = 1.3` combined with an explicit `Tls12CipherList`
    is contradictory** and fails the handshake with "no ciphers available":
    OpenSSL version-filters the (now unusable) TLS 1.2 cipher list to empty.
    Set only `Tls13CipherSuites` when requiring TLS 1.3. This surfaces only
    for the below-default-strength CCM_8 suites (which need `@SECLEVEL=0`);
    no mainstream HTTPS configuration is affected.

## Protocol scope

15. HTTP/1.1 and (flag-gated) HTTP/2 only. No HTTP/3, no WebSockets, no
    FTP/SMTP/other libcurl protocols (locked out via
    `CURLOPT_PROTOCOLS_STR = "http,https"`).

## Resource bounds and buffering (from the deep review)

17. **Peak response-body memory** is `MaxResponseBufferBytes` plus up to one
    `ReceiveBufferSize` chunk (plus `ArrayPool` power-of-two rounding), not
    exactly `MaxResponseBufferBytes` — the queue always admits one chunk when
    empty to avoid wedging. On the `MultiEventLoop` engine, add up to one
    HTTP/2 stream window for a stalled h2 download.
18. **Response header size** is bounded by `MaxResponseHeadersLength` (default
    1 MiB, all header lines + trailers); a server exceeding it fails the
    transfer. libcurl separately caps a single header line at 100 KiB.
19. **A request content stream that ignores its cancellation token** pins one
    background task plus a 64 KiB pooled buffer until the stream itself returns
    or faults; a stream that blocks forever leaks those until process exit.
    Well-behaved streams (and `HttpClient`, which disposes request content
    after the send) are unaffected.
20. **Disposing the handler while a cancellation-ignoring request body is
    mid-transfer** makes `Dispose` wait on the native drain rather than return
    immediately — this is deliberate (destroying the native client with a live
    request in flight would be a use-after-free).

## Security configuration notes (from the deep review)

21. **Cipher-string escape hatch**: `Tls12CipherList`/`Tls13CipherSuites` are
    passed to OpenSSL verbatim, so an embedded `@SECLEVEL=0` weakens what the
    (always-on) certificate verification will accept. Only set these
    deliberately. Certificate and hostname verification themselves can never
    be disabled.
22. **CA bundle is read live** (`CURLOPT_CAINFO` path) and re-read across new
    connections, so a party able to *write* the bundle file can inject a trust
    anchor mid-lifetime. The trust boundary is the filesystem ACL on the bundle
    path. (Set `CURLHTTP_CA_BLOB=1` for the older snapshot-at-construction
    behavior, at the cost of per-connection re-parsing.)
23. **Credentials in a request URL's userinfo** (`https://user:pass@host/`) are
    not stripped from logs or from `RequestMessage.RequestUri`. Pass
    credentials via headers, not the URL.
