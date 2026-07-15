# Troubleshooting

First move, always: `handler.GetDiagnostics()` (or run
`samples/CurlHttpClient.Sample`) and `curl_bridge_spike.exe` — between them
they isolate managed-vs-native-vs-OS problems in seconds.

## Startup failures (`CurlHttpInitializationException`)

| Message contains | Cause | Fix |
| --- | --- | --- |
| `curl_http_bridge.dll was not found` | native assets not deployed | ensure `runtimes\win-x64\native\curl_http_bridge.dll` beside the app, or set `NativeLibraryPath` |
| `architecture mismatch` / `built for x64` | 32-bit process | run the app as x64 (`<PlatformTarget>x64</PlatformTarget>`) |
| `TLS backend is ... not OpenSSL` | wrong/rebuilt DLL, or a foreign libcurl loaded | verify DLL SHA-256 against the package manifest; check `NativeLibraryPath` |
| `lacks the threaded resolver` | native rebuilt without AsynchDNS | rebuild with the pinned vcpkg manifest |
| `No certificate trust source` | cacert.pem missing and no CA option set | deploy cacert.pem, or set `CertificateAuthorityBundlePath` / `UseSystemCertificateStore` |
| `cipher list rejected` | bad `Tls12CipherList`/`Tls13CipherSuites` | fix the OpenSSL cipher string (validated at startup on purpose) |
| DLL load fails only on 2012 R2 | import gate bypassed / non-v142 rebuild | run `build\check-imports.ps1`; rebuild with the v142 preset |

## Request failures

| Exception | Meaning | Notes |
| --- | --- | --- |
| `HttpRequestException` (`SecureConnectionError`) + `AuthenticationException` | certificate/hostname rejected or handshake failed | message carries the libcurl detail (e.g. `unable to get local issuer certificate` → CA bundle lacks the issuer; hostname mismatch names the cert). Never "fix" by disabling verification — there is deliberately no such option; fix the trust chain. |
| `HttpRequestException` (`NameResolutionError`) | DNS | check resolver/hosts; the handler never uses env-var proxies — a proxy expected from `HTTP_PROXY` must be configured via options |
| `HttpRequestException` (`ConnectionError`) + `TimeoutException` | connect timeout | raise `ConnectTimeout`; check firewall/routing |
| `TaskCanceledException` + `TimeoutException` | `RequestTimeout` (or `HttpClient.Timeout`) elapsed | distinguish via which timeout you configured |
| `TaskCanceledException` (no inner) | caller token cancelled | expected behaviour |
| `IOException` while reading the response stream | transfer died mid-body (`Transferred a partial file`, reset, …) | server-side close/truncation; buffered data before the failure was delivered |
| `worker pool exhausted` (`HttpRequestException`) | all `MaxConcurrentRequests` workers pinned by open streaming responses | consume/dispose response streams; raise `MaxConcurrentRequests` |
| `ObjectDisposedException` from the stream | response was disposed while streaming | expected when abandoning a response early |

Every mapped exception also carries `Data["CurlErrorCode"]` (raw CURLcode)
and `Data["CurlBridgeResult"]` for support tickets.

## Diagnosing with verbose logging

```csharp
var options = new CurlHttpClientOptions { EnableNativeVerboseLogging = true };
// pass an ILogger<CurlHttpMessageHandler>; lines appear at Debug level:
//   curl ** Connected to host ...
//   curl >> GET /api HTTP/1.1          (request headers, redacted)
//   curl << HTTP/1.1 200 OK            (response headers, redacted)
```

Bodies are never logged. For wire-level traces use the `CurlHttpClient`
EventSource: `dotnet-trace collect --providers CurlHttpClient`.

## Native-level isolation

`curl_bridge_spike.exe [url]` runs the whole native stack without .NET:
- spike fails → OS/build problem (imports, TLS, network) — .NET is innocent.
- spike works but the app fails → managed configuration (options, DI,
  deployment layout).

## Performance symptoms

- Requests queue behind slow consumers → see worker-pool exhaustion above.
- No connection reuse (EventSource `connectionReused=false` on repeats) →
  expected across *concurrent* requests (no cross-thread sharing; see
  docs/architecture.md); for sequential requests check the server sends
  keep-alive and `PooledConnectionIdleTimeout` isn't too aggressive.
- High memory during downloads → consumer isn't reading; buffering is capped
  at `MaxResponseBufferBytes` (default 1 MiB) per in-flight response.
