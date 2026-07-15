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

## Architecture (Option A: blocking worker pool)

4. **No HTTP/2 multiplexing and no cross-thread connection sharing** —
   libcurl only multiplexes/shares within one easy/multi handle. N concurrent
   requests to one origin = up to N connections. HTTP/2 (`EnableHttp2`) is
   therefore off by default and purely a protocol-compatibility switch.
   The C ABI is shaped so a `curl_multi` event-loop backend can lift this
   without public API changes.
5. Each in-flight request (including an open streaming response) pins one
   dedicated worker thread; admission beyond `MaxConcurrentRequests` fails
   fast after `WorkerAdmissionTimeout`.
6. Synchronous `HttpClient.Send` is not supported (async-only handler).

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

## Protocol scope

15. HTTP/1.1 and (flag-gated) HTTP/2 only. No HTTP/3, no WebSockets, no
    FTP/SMTP/other libcurl protocols (locked out via
    `CURLOPT_PROTOCOLS_STR = "http,https"`).
