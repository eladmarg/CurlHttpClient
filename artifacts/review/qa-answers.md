# Deep review — answers to the required questions

Each answer references the consolidated finding id (M-nn, see findings.md) and/or
the fixing commit. "Both engines" = the dedicated-worker default and the opt-in
curl_multi event-loop engine.

## Memory / lifetime

**Are there any known managed memory leaks?**
No. The per-request GCHandle, pooled upload buffer, body-read CTS and caller
registration are freed exactly once through `ReleasePerRequestResources`
(Interlocked-guarded, S3.2). Two leak paths found in review are fixed: the
pre-submit multi path that never aborted the upload queue/`_active` entry
(M13/A2-7, S3.3) and the orphaned upload pump (M12, S3.2). `_perOrigin`
grows one small semaphore per distinct origin and is documented as bounded
(M30); it is only populated when `MaxConnectionsPerServer > 0`.

**Are there any known native memory leaks?**
No known leaks. `curl_slist` ownership (including the bridge-generated
`extra_headers` tail) is freed on every path, with the orphan-path gap closed
(M25, S3.1). Easy handles are pooled and cleaned up at client destroy; the
multi client joins its loop thread and cleans up the CURLM. `/analyze` reports
zero warnings (S5). `curl_global_cleanup` is intentionally not called
(process-lifetime model, documented).

**Can any native callback run after managed state is released?**
No. The pooled easy handle is `curl_easy_reset` at *release* (clearing all
callbacks and `CURLOPT_PRIVATE`) before it can be reused, and the GCHandle is
freed only after `on_complete`/after the blocking send returns (refuted
hypotheses, A1). Stale queued Cancel/Unpause commands are pointer-compared
against a loop-thread-only `active` set and never dereference a freed request
(FIFO-ordering invariant, documented in code, M22).

**Can any pooled buffer be used after return?**
No. Segment buffers are dequeued-and-returned atomically under the queue lock;
`_uploadBuffer`'s grow path now nulls the field before returning the old array
(M20, S3.4); the M1 double-return is eliminated by single-owner cleanup (S3.2).

**Can any resource be released twice?**
No. GCHandle/buffer/CTS release is Interlocked-once (M1, S3.2);
`finish_request` is guarded by a `finished` flag so `on_complete` fires once
(M21, S3.1); SafeHandle/registration disposal is idempotent by contract.

## Deadlock / shutdown

**Can handler disposal deadlock?**
No. Worker Dispose drains the queue and fails orphans (M2), releases go through
tolerant `TryRelease` and the semaphores are not disposed (M3), parked
admission waiters are woken via a linked `_disposeCts` (M5). `client_destroy`
waits on `active_requests` with a 1 ms sleep — a correctness requirement
(destroying with a live request is a UAF), bounded in practice by the managed
drain (M4). All S3.3.

**Can cancellation deadlock?**
No. The completion-vs-cancel stall from `CancellationTokenRegistration.Dispose`
blocking on a running callback was found during validation and fixed by using
`Unregister` (M32, S3.3). `Cancel()` is idempotent across the caller token,
stream disposal and handler disposal (refuted A2-R5).

**Can application shutdown deadlock?**
No. The loop thread's completion callbacks are non-blocking by construction
(pause-based writes + `RunContinuationsAsynchronously`), the finalizer is not
awaited at process exit, and there is no `DllMain`/static-destructor state
(A3 Q10). A submit racing shutdown is completed CANCELLED by the loop's final
drain instead of hanging (M-H/A3-4, S3.1).

**Can `curl_global_cleanup()` race active requests?**
It cannot — it is never called (process-lifetime init via `std::call_once`,
documented as intentional).

## Callbacks / handles

**Is every callback delegate correctly rooted?**
Yes. Callbacks are `[UnmanagedCallersOnly]` static function pointers — nothing
to root or collect; per-request state travels as a GCHandle.

**Is every `GCHandle` released exactly once?**
Yes — `AllocateSelfHandle` is called once per request; the free is
Interlocked-once in `ReleasePerRequestResources` (S3.2).

**Is every native handle protected by deterministic ownership?**
Yes — each is a `SafeHandle` (`CurlBridgeClientHandle`,
`CurlBridgeMultiClientHandle`, `CurlBridgeRequestHandle`) with idempotent
`ReleaseHandle`.

## Bounds / locks

**Are response and upload buffers bounded?**
Yes. Response body: `MaxResponseBufferBytes` + one `ReceiveBufferSize` chunk
(now documented, M30/A4-6). Response headers: newly bounded by
`MaxResponseHeadersLength` (M16, S3.5). Upload queue: `max(UploadBufferSize,
64 KiB) × 2`. The h2 upstream pause-buffering caveat is documented.

**Are all locks necessary? Which were removed? Which retained and why?**
No locks were removed; none were found redundant. Retained: `BoundedByteQueue._sync`
(single monitor over all queue state + rendezvous), the dispatchers'
`SemaphoreSlim` admission gates, `_threadSync` (worker list), and native
`pool_mutex`/`share_locks`/`cmd_mutex`. Each has a documented single purpose.

**Are any lock-free algorithms present? How proven correct?**
Only simple `Interlocked` terminal-state transitions (`_completed`,
`_resourcesReleased`, `_disposeGuard`) and atomics on the native side. No
hand-rolled lock-free queues. The lock-order graph was proven acyclic (A5):
every cross-domain edge points managed→native and every native→managed
transition happens with no native lock held.

**Is nullable reference analysis enabled and clean?**
Yes — `<Nullable>enable</Nullable>` repo-wide with `TreatWarningsAsErrors`;
the build is clean.

## Language / build

**What C# language version is used and why?**
C# 14, pinned explicitly (S6). It is the .NET 10 SDK default (no code churn),
and pinning prevents a future `global.json` bump from silently shifting
semantics on a certified codebase.

**Which modern features were introduced / rejected?**
Introduced: nothing new syntactically in the fixes (the code already used
file-scoped namespaces, collection expressions, pattern matching); the review
prioritized correctness over syntax. Rejected: converting `CurlHttpClientOptions`
to a record / adding `required` (breaking public-API change), and a full
analyzer-as-error rollout (deferred as a separate mechanical pass to avoid
entangling it with safety fixes — .editorconfig records the posture).

**What C++ standard is used? Are C++ exceptions fully contained?**
C++17. Yes — every `extern "C"` export and the loop-thread entry now catch all
exceptions (no exception crosses the C ABI); catch handlers do not allocate
(M7/M8/M9, S3.1). `/analyze` and `/W4 /WX` are clean (S5/S6).

**Is the ABI stable and versioned?**
Yes — struct-size-gated structs, fixed-width fields, cdecl throughout, no
`bool`/`long` on the boundary. Verified field-by-field (A3 Q12) and by the
native ABI contract test.

## Security

**Are certificate and hostname validation always enabled?**
Yes — `CURLOPT_SSL_VERIFYPEER=1` / `VERIFYHOST=2` unconditionally, with no
config path to disable them (A6 refutation). The only trust-weakening surface
is an explicit `@SECLEVEL` in a caller-supplied cipher string, documented
(M30/A6-5).

**Is OpenSSL proven to be the runtime TLS backend?**
Yes — `curl_global_sslset(OPENSSL)` before init, native `validate_build`
fail-fast, and a managed re-check.

## Verification

**Do all tests pass?**
Yes. Unit 74; integration 333 on both engines; gated stress (100 MB
bounded-memory, soak, churn, concurrency ladder) on both engines; native ABI
test; WS2012R2 import gate; consumer offline suite.

**Was the final build tested on Windows Server 2012 R2?**
Not on physical hardware (no VM available — a standing project constraint).
The on-target proxy is enforced: the rebuilt `/WX` DLL passes the import gate
(only Windows 8.1-floor system DLLs; static CRT) and the native ABI test. The
documented on-target checklist (docs/deployment-ws2012r2.md) is the release
gate for an actual 2012 R2 deployment. Recorded as an explicit caveat.

**Is the implementation safe to release?**
Yes, with the documented caveats — see verdict.md.
