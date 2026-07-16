# Rejected optimizations

Candidates that were measured or analyzed and **not** adopted, with the reason.
Recorded so the decisions aren't relitigated.

## Buffer-size default changes (P5) — measured, no better balanced default
Swept `CURLOPT_BUFFERSIZE` {64/256/512 KB} × download sizes and
`CURLOPT_UPLOAD_BUFFERSIZE` {64 KB/256 KB/1 MB} × upload sizes at loopback and
under a slow consumer (`artifacts/performance/optimized/buffer-sweep.md`). No
non-default value improved one profile without regressing another:
- **1 MB upload buffer** cut the read-callback count on an 8 MB upload but
  *regressed* small/latency-sensitive uploads and raised per-request memory —
  a clear loss under the balanced-tuning constraint. Rejected as a default;
  exposed as the opt-in `UploadBufferSize` knob instead (P4).
- Larger receive buffers didn't move loopback download throughput measurably
  and cost memory. Kept the 256 KB receive / 64 KB upload defaults.

## Selective `setopt` reuse without `curl_easy_reset` — correctness footgun
Skipping `curl_easy_reset` and re-setting only changed options between pooled
requests would save a handful of µs of `setopt` calls. Rejected: option state
leaking between unrelated requests (headers, method, body, callbacks, auth) is
a correctness/security hazard far outweighing a µs-level gain. The full reset
stays.

## Micro-items on the small-GET path — sub-µs, not worth complexity
The +70 µs gap vs `SocketsHttpHandler` on reused small GETs is dominated by
thread-hop latency (worker handoff + TCS continuation hops), which only the
event-loop engine addresses (P7). The remaining candidates are each µs-or-less
and were rejected as not worth the code complexity / risk:
- GCHandle alloc per request, the ~11 P/Invoke calls to configure a request,
  native `new`/`delete` per request, header-line string interning.
- These were confirmed structurally µs-level and are noise against the
  ~190 µs floor.

## Custom `IValueTaskSource` for the response stream — complexity ≫ benefit
A pooled value-task-source could shave the per-read `Task` on the async stream
path, but the async read path already allocates little (P2/P3 removed the big
items), and a hand-rolled `IValueTaskSource` with correct multi-consumer /
cancellation semantics is a large, bug-prone surface. Rejected; the
Monitor/rendezvous design carries the streaming path.

## Header-parse micro-optimizations beyond P2e — required by contract
Fully avoiding header string materialization is impossible: `HttpHeaders`
requires `string` name/value pairs for the final response. P2e already moved
the status/blank-line detection to spans; going further would mean caching or
interning that doesn't pay off against real header sets. Rejected.

## Retiring the managed per-origin gate on the multi engine — kept for parity
The plan floated replacing the managed per-origin semaphore with
`CURLMOPT_MAX_HOST_CONNECTIONS` on the event-loop engine. Kept the managed gate:
it is identical in behavior across both engines (one code path to reason about),
already correct, and the native option's queued-timeout semantics differ subtly.
The admission-timeout-while-queued caveat is documented instead.
