# Performance notes

Measured with BenchmarkDotNet (short job) on the build machine, against an
in-process Kestrel server over **plain HTTP**, so the numbers compare handler
pipelines rather than two different TLS stacks. `SocketsHttpHandler` is the
baseline. Reproduce with:

```
dotnet run --project benchmarks\CurlHttpClient.Benchmarks -c Release -- --job short
```

| Scenario | SocketsHttpHandler | CurlHttpMessageHandler | Ratio |
| --- | --- | --- | --- |
| Small JSON GET (sequential) | 125.3 µs / 2.5 KB alloc | 196.5 µs / 5.0 KB alloc | 1.57× |
| 32 concurrent small GETs | 1,112.6 µs | 603.2 µs | 0.54× |
| 32 MB download (streamed) | 32.09 ms / 431 KB | 29.18 ms / 589 KB | 0.91× |
| 8 MB upload | 9.46 ms / 83 KB | 9.98 ms / 94 KB | 1.06× |

Reading the numbers:

- **Per-request overhead** (~70 µs) comes from the P/Invoke boundary,
  per-request native-handle configuration, and the hop to a dedicated worker
  thread. Irrelevant for real workloads: one TLS handshake costs 10–100× more,
  and this library exists precisely for endpoints Schannel can't reach at all.
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
| `EnableHttp2` | off | h2 adds no multiplexing benefit in this architecture (each request = own connection); prefer HTTP/1.1 keep-alive unless the server requires h2 |

## Architectural limits (see docs/architecture.md)

- No cross-thread connection sharing: N *concurrent* requests to one origin
  use up to N connections. Sequential requests reuse pooled handles'
  keep-alive connections.
- One dedicated thread per in-flight request (~1 MB stack reserve each). At
  the default pool sizes this is noise; for very high concurrency the
  planned `curl_multi` backend is the right fix, not a bigger pool.
