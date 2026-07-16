# Performance optimization — before / after

Evidence-driven optimization program (stages P0–P7). Every figure below comes
from the repository's precise harness
(`benchmarks/CurlHttpClient.Benchmarks` → `harness`): single-thread, warm
connection pool, `Stopwatch` percentiles, `GC.GetAllocatedBytesForCurrentThread`
for allocation. Machine: Windows 11 x64, .NET 10.

Three columns:

- **Baseline** — before any optimization (`artifacts/performance/baseline/`).
- **Workers** — after P1–P6, default `DedicatedWorkers` engine
  (`artifacts/performance/optimized/perf-harness-workers.json`).
- **Multi** — after P7, opt-in `MultiEventLoop` engine
  (`artifacts/performance/optimized/perf-harness-multi.json`).

## Latency (median µs) and allocation (bytes/op)

| Scenario | Base µs | Work µs | Multi µs | Base B/op | Work B/op | Multi B/op |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| small-reused-get-http | 189.3 | 190.7 | 199.4 | 620 | 578 | 612 |
| small-reused-get-https | 203.4 | 218.7 | 223.1 | 893 | 1,175 | 644 |
| ttfb-headers-read | 206.1 | 189.0 | 175.1 | 980 | 566 | 526 |
| small-json-post | 243.0 | 187.6 | 175.5 | 1,607 | 1,460 | 1,487 |
| **sync-send-get** | 202.2 | 171.3 | 152.2 | **264,091** | **1,808** | **2,119** |
| download-32mb | 32,735 | 30,107 | **25,316** | — | — | — |
| upload-8mb | 7,752 | 7,536 | 7,344 | 6,134 | — | — |
| new-connection-https-get | 2,056 † | 2,080 † | 2,170 † | 2,011 | — | — |
| **cancellation-latency** | 979,293 | 980,112 | **257** | 0 | 0 | 0 |
| handler-create-dispose | 165.9 | 119.4 | 403.2 | 14,608 | 13,580 | 12,152 |

Download/upload allocation is GC-noisy on a single thread (the delta goes
negative when the GC reclaims body buffers mid-measurement), so those cells are
omitted; throughput is the meaningful metric there and copies/byte was reduced
structurally (P3, see retained-optimizations.md).

† The main-suite `new-connection-https-get` number does **not** capture the CA
fix: the baseline harness predates the realistic ~200 KB bundle and used a
tiny bundle in every column, so the X509 re-parse cost is invisible here. The
CA fix was measured directly, blob vs path on the realistic bundle:

| CA delivery | new-connection-https-get median µs |
| --- | ---: |
| `CURLOPT_CAINFO_BLOB` (before, cache-disqualified) | 9,562 |
| `CURLOPT_CAINFO` path (after, cache eligible) | 2,435 |

**3.9× faster per new TLS connection** once the OpenSSL X509-store cache warms.
Sources: `perf-p1-blob.json`, `perf-p1-path.json`.

## Headline results

1. **Sync `HttpClient.Send` allocation: 264,091 → 1,808 B/op (≈146× less).**
   `CurlResponseStream.Read(Span)` allocated an ~80 KB array plus a `Task<int>`
   per read; sync buffering drove it hard. Replaced with a Monitor-blocking
   queue read (no temp array, no Task). Latency also improved 202 → 171 µs. (P2a)

2. **New TLS connection: 3.9× faster** (9,562 → 2,435 µs) by delivering the CA
   bundle as a file path (`CURLOPT_CAINFO`) instead of a blob, which re-enables
   OpenSSL's per-handle X509-store cache. (P1)

3. **Cancellation during a header-wait: 979 ms → 0.26 ms (≈3,800×)** on the
   event-loop engine. The worker engine can only observe a cancel at libcurl's
   ~1 s progress cadence when no data is flowing; `curl_multi_wakeup` makes it
   instant. (P7)

4. **32 MB download: 32.7 → 25.3 ms (−23%)** on the event-loop engine; −16% vs
   the optimized worker engine. Single shared connection pool + no per-request
   thread handoff. (P3 + P7)

5. **Time-to-headers and JSON POST** improved on both engines
   (206 → 175–189 µs; 243 → 176–188 µs) with lower allocation, from the
   hot-path fixes (lazy body CTS, reused upload buffer, span-based header parse,
   `ValueTask` sync fast path). (P2)

## Honest non-movers

- **small-reused GET is flat** (≈190 µs, ≈600 B/op) across all three columns.
  The remaining gap to `SocketsHttpHandler` (~125 µs) is thread-hop latency
  (worker handoff + TCS continuation hops) on the worker engine, and the loop
  round-trip on the multi engine — an architectural floor, not addressable by
  micro-optimization. Confirmed by rejecting the micro items (see
  rejected-optimizations.md). The multi engine is ~9 µs slower here because a
  single serialized request pays the loop hop without the concurrency/connection
  benefits that motivate it.

- **handler-create-dispose is slower on the multi engine** (119 → 403 µs)
  because creating the handler spins up the loop thread. This is a one-time
  cost per (long-lived, shared) handler and does not touch the request path.

## Engine selection

`DedicatedWorkers` remains the **default**: it is at parity or better on the
single-request latency/allocation profile and has no per-handler startup cost.
`MultiEventLoop` is **opt-in** (`CurlHttpClientOptions.ExecutionEngine`) and
wins decisively for near-instant cancellation, high concurrency to one origin,
HTTP/2 multiplexing, and consolidated connection reuse. Both engines pass the
entire certification suite (317 behavioral + 16 gated stress/bounded-memory).
