# Performance notes

Two measurement surfaces:

1. **Comparison vs `SocketsHttpHandler`** (BenchmarkDotNet, in-process Kestrel
   over **plain HTTP**, so the numbers compare handler pipelines rather than two
   TLS stacks):
   ```
   dotnet run --project benchmarks\CurlHttpClient.Benchmarks -c Release -- --job short
   ```
2. **Optimization program** (precise single-thread harness — `Stopwatch`
   percentiles + `GC.GetAllocatedBytesForCurrentThread`), the authoritative
   before/after for stages P1–P7. Run per engine:
   ```
   dotnet run --project benchmarks\CurlHttpClient.Benchmarks -c Release -- harness out.json label
   set CURLHTTP_ENGINE=multi   # to benchmark the event-loop engine
   ```
   Full results and analysis: `artifacts/performance/comparison/before-after.md`,
   `retained-optimizations.md`, `rejected-optimizations.md`.

| Scenario | SocketsHttpHandler | CurlHttpMessageHandler | Ratio |
| --- | --- | --- | --- |
| Small JSON GET (sequential) | 125.3 µs / 2.5 KB alloc | 196.5 µs / 5.0 KB alloc | 1.57× |
| 32 concurrent small GETs | 1,112.6 µs | 603.2 µs | 0.54× |
| 32 MB download (streamed) | 32.09 ms / 431 KB | 29.18 ms / 589 KB | 0.91× |
| 8 MB upload | 9.46 ms / 83 KB | 9.98 ms / 94 KB | 1.06× |

(The BDN table above predates the optimization program; the precise harness
puts a reused GET at ~190 µs / ~600 B/op after P2–P3.)

Optimization-program highlights (precise harness, medians):

- **Sync `HttpClient.Send` allocation: 264 KB/op → 1.8 KB/op** (≈146×) — removed
  a per-read array+`Task` in the sync stream path (P2a).
- **New TLS connection 3.9× faster** (9.6 → 2.4 ms) — CA bundle by path re-enables
  OpenSSL's X509-store cache (P1). Caveat: `UseSystemCertificateStore`
  (`CURLSSLOPT_NATIVE_CA`) disqualifies that cache — expect the higher cost then.
- **Cancellation during a header-wait: 979 ms → 0.26 ms** on the event-loop
  engine (P7).
- **32 MB download −16%** on the event-loop engine (P7).

Reading the numbers:

- **Per-request overhead** (~70 µs on the worker engine) comes from the P/Invoke
  boundary, per-request native-handle configuration, and the hop to a dedicated
  worker thread. Irrelevant for real workloads: one TLS handshake costs 10–100×
  more, and this library exists precisely for endpoints Schannel can't reach at
  all. The event-loop engine trades the thread hop for a loop round-trip
  (parity on this path, wins elsewhere — see below).
- **Throughput is at parity** for large transfers in both directions — the
  bounded-queue streaming path adds no measurable cost at 1+ GB/s rates.
- **Concurrency scales well**: 32 parallel requests complete faster than the
  baseline because each has a dedicated blocked thread (no ThreadPool
  interaction), at the price of those threads existing.
- Allocations are ~2× baseline per request (a few KB): header line strings
  and per-request context. Body chunks are `ArrayPool`-recycled; steady-state
  streaming allocates almost nothing per chunk.

## Tuning knobs

| Option | Default | Effect |
| --- | --- | --- |
| `MaxConcurrentRequests` | max(8, 2×cores) | worker pool = max in-flight requests; each open streaming response pins one worker |
| `MaxResponseBufferBytes` | 1 MiB | per-response buffering before backpressure pauses the transfer; raise for fast-producer/slow-consumer patterns |
| `ReceiveBufferSize` | 256 KiB | libcurl receive-buffer (`CURLOPT_BUFFERSIZE`); bigger = fewer native→managed transitions on fast downloads |
| `PooledConnectionIdleTimeout` / `PooledConnectionLifetime` | libcurl default / 15 min | keep-alive reuse window vs DNS-staleness bound |
| `EnableHttp2` | off | on the default worker engine h2 adds no multiplexing benefit (each request = own connection); on the `MultiEventLoop` engine h2 streams multiplex onto one shared connection |
| `UploadBufferSize` | 0 (libcurl 64 KiB) | `CURLOPT_UPLOAD_BUFFERSIZE`; larger cuts read-callback count on big uploads but costs memory and hurts small uploads — left at default (see rejected-optimizations.md) |
| `ExecutionEngine` | `DedicatedWorkers` | `MultiEventLoop` = single loop thread, shared connection pool, h2 multiplexing, instant cancellation; opt-in (see below) |

## Execution engines

- **`DedicatedWorkers` (default)** — a bounded pool of blocking threads, one
  transfer each. Best single-request latency/allocation, no per-handler startup
  cost. No cross-thread connection sharing: N *concurrent* requests to one
  origin use up to N connections (sequential requests reuse pooled keep-alive
  connections). One dedicated thread per in-flight request (~1 MB stack reserve).
- **`MultiEventLoop` (opt-in)** — one loop thread drives all transfers through a
  single `curl_multi` handle. Connections consolidate into one shared pool,
  HTTP/2 streams multiplex, and cancellation is near-instant
  (`curl_multi_wakeup`, 0.26 ms vs 979 ms on a header-wait). Choose it for high
  concurrency to one origin, h2 multiplexing, or cancellation-sensitive
  workloads. Costs: ~300 µs one-time per-handler loop-thread startup, and h2
  upstream pause-buffering can exceed `MaxResponseBufferBytes` by up to one
  stream window (~10 MB) on a stalled download — see docs/limitations.md.
