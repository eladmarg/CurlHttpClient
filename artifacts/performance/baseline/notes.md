# Baseline performance notes

Captured via `dotnet run -c Release -- harness` (single-thread precise harness;
`GC.GetAllocatedBytesForCurrentThread` + Stopwatch percentiles). Machine:
Windows 11 x64, .NET 10, workstation GC. See perf-harness.json for raw data.

| Scenario | median µs | p99 µs | alloc B/op |
| --- | ---: | ---: | ---: |
| small-reused-get-http | 189.3 | 327.7 | 620 |
| small-reused-get-https | 203.4 | 346.0 | 893 |
| ttfb-headers-read | 206.1 | 316.0 | 980 |
| small-json-post | 243.0 | 386.5 | 1,607 |
| **sync-send-get** | 202.2 | 2361.1 | **264,091** |
| download-32mb | 32,735 | 37,981 | (GC-noisy) |
| upload-8mb | 7,752 | 10,753 | 6,134 |
| **new-connection-https-get** | **2,056** | 3,151 | 2,011 |
| **cancellation-latency** | **979,293** | 986,204 | 0 |
| handler-create-dispose | 165.9 | 254.7 | 14,608 |

## Findings driving the optimization stages

1. **sync-send-get allocates 264 KB/op** — `CurlResponseStream.Read(Span)` allocates
   `new byte[buffer.Length]` (~80 KB) + a `Task<int>` per call; sync `HttpClient.Send`
   buffering drives it. Top managed-allocation fix (P2a).
2. **new-connection-https-get 2.0 ms/op** — a fresh TLS handshake re-parses the
   ~200 KB CA bundle because `CURLOPT_CAINFO_BLOB` disqualifies OpenSSL's X509-store
   cache. P1 fixes it via `CURLOPT_CAINFO` path.
3. **cancellation-latency ~979 ms** — during a pure header-wait (connected, no data
   flowing) cancellation is only observed at libcurl's ~1 s progress-callback cadence.
   Instant during data transfer (write callback checks the flag). Only the curl_multi
   engine (`curl_multi_wakeup`, P7) makes the header-wait case instant.
4. **small-reused async path already lean** (620–980 B/op). The +70 µs vs
   SocketsHttpHandler is thread-hop latency (worker handoff + TCS hops), a floor of
   the worker architecture until P7.
5. **download-32mb alloc/op is GC-noisy** (single-thread delta goes negative when GC
   reclaims body buffers mid-window) — throughput (32.7 ms) is the meaningful download
   metric; copies/byte is measured structurally, not via this figure.
6. **upload-8mb** already lean on allocations; the ~128 read-callbacks (64 KB buffer)
   are the CPU story — P4 raises `UPLOAD_BUFFERSIZE`.
