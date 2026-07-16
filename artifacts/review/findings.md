# Deep review — findings ledger (append-only)

## Resolution summary (all fixed)

| Finding(s) | Severity | Fixing commit |
|---|---|---|
| M1 (=G), M6 (=I), M12 | Critical / High | S3.2 single-owner cleanup |
| M2, M3, M4, M5, M11, M15, M28, M32 | Critical / High / Med | S3.3 dispatcher dispose |
| M7, M8, M9, M10-native, M-H, M21, M22, M24, M25, M29 | High / Med / Low | S3.1 native ABI hardening |
| M14, M19, M20, M26, M27 | Med / Low | S3.4 queue/pool |
| M10-managed, M16, M18, M23 | Med / Low | S3.5 validation + redaction |
| M30 (docs), M31 (tests) | Low | S3.5 code + S4 tests + docs |
| Seeds F, H, J, K, E | — | REFUTED / downgraded (documented) |

Validated: unit 74; integration 333 ×2 engines; gated stress ×2 engines; native
ABI test; WS2012R2 import gate; no perf regression. See verdict.md.

---


Status values: `OPEN` (raised, unverified) → `CONFIRMED` (repro or airtight interleaving) /
`REFUTED` (guard found; recorded below) / `UNPROVEN-HARDEN` (no repro, invariant unenforced,
cheap hardening justified) → `FIXED (<commit>)` / `DOCUMENTED`.

Severity: Critical / High / Medium / Low / Info.

## Seed findings (raised during planning, pre-fan-out)

| ID | Severity | Status | Claim |
|---|---|---|---|
| G | Critical | CONFIRMED (planning trace) | Failed multi request: response-TCS fault escapes `CurlMultiDispatcher.DispatchAsync` before cleanup → handler `finally` runs `OnWorkerFinished()` concurrently with loop thread's `OnMultiFinished()` → racy double `GCHandle.Free` / double `ArrayPool.Return(_uploadBuffer)` / double `_bodyReadCts.Dispose` |
| A | High | OPEN | Native multi exports without try/catch; `enqueue()` can throw `bad_alloc` across the C ABI → std::terminate |
| B | High | OPEN | `CurlMultiDispatcher.Dispose` disposes semaphores before native destroy drains completions → `OnFinished` releases disposed semaphore → cleanup skipped, request-handle leak to finalizer |
| C | Medium | OPEN | `static_cast<long>` LLP64 narrowing of int64 ms timeouts / connection ages (silent wrap > ~24.8 days) |
| D | Low | OPEN | `size*nmemb` unchecked multiply in native callbacks (theoretical; libcurl caps chunk size) |
| E | Medium | OPEN | `_bodyReadCts` disposed in `OnMultiFinished` while `PumpUploadAsync` may still read `.Token` |
| F | Medium | OPEN | Sync `Send` ThreadPool-starvation behavior uncharacterized on the multi engine |
| H | High | OPEN | Submit command enqueued after loop's final `process_commands()` is never processed → `SendAsync` hangs forever + GCHandle leak |
| I | High | OPEN | Post-submit `MultiCancel` on already-disposed request SafeHandle → ODE → `finally` double-releases `_slots` |
| J | High | OPEN | After `BoundedByteQueue.Abort`, `TryWrite`-false maps to native PAUSE (2) not abort → transfer paused forever unless every Abort caller also natively cancels |
| K | High | OPEN | Native easy-handle pool cap (`min(MaxConcurrentRequests,16)`) below multi-engine admission (`MaxConcurrentRequests`) → INTERNAL_ERROR at >16-way multi concurrency |
| L | Medium | OPEN | `Options.Validate()` gaps: `UploadBufferSize` unbounded (capacity int overflow), negative timeouts, `MaxConnectionsPerServer` |

## Fan-out findings (S1 agents A1–A6)

### A6 — Security/TLS/lifecycle (agent completed)

| ID | Severity | Status | Claim |
|---|---|---|---|
| A6-1 | Medium | OPEN | Response header count/size unbounded in managed memory: `ResponseHeaderParser._headers`/`_trailers` have no cap (libcurl caps one line at 100 KB, not the count) → header-flood OOM; SocketsHttpHandler has `MaxResponseHeadersLength`, we have no equivalent |
| A6-2 | Medium | OPEN | No CRLF/control-char validation on request header values: `TryAddWithoutValidation`-smuggled `\r\n` reaches `curl_slist_append` verbatim → request splitting; SocketsHttpHandler enforces at send time |
| A6-3 | Medium | OPEN | Proxy **username** leaks via libcurl free-form TEXT verbose lines (`Proxy auth using Basic with user 'alice'`) — `RedactHeaderLine` only matches header prefixes; password itself is covered |
| A6-4 | Low | OPEN | `security-review.md` stale: default CA trust is now a live `CURLOPT_CAINFO` path (P1), not an immutable blob — mid-lifetime CA-file tamper window; docs must state the ACL boundary |
| A6-5 | Low | OPEN | `Tls12CipherList`/`Tls13CipherSuites` passed raw: `@SECLEVEL=0` silently weakens cert-strength requirements while verify flags stay on — validate or document as audited escape hatch |
| A6-6 | Low | OPEN (accept/doc) | Client-key passphrase unzeroized in native `std::string` for client lifetime — true zeroization unachievable (SSO/realloc copies); document as accepted risk (`bridge_internal.h:30`, `bridge_request.cpp:286-289`) |
| A6-7 | Info | REFUTED | Native error detail carries host:port only, not full URL/query/credentials — no query-secret leak via exception messages (`bridge_error.cpp:101-128`, `CurlErrorMapper.cs:30-99`) |
| A6-url | Low | OPEN | `LogRedaction.RedactUrl` strips query but not `user:pass@` userinfo → leaks to logs + `RequestMessage.RequestUri` (`LogRedaction.cs:31-48`, `CurlRequestContext.cs:667-672`) |

**A6 refuted (guards confirmed):** VERIFYPEER=1/VERIFYHOST=2 unconditional, no disable path (`bridge_request.cpp:236-237`); no-trust-source refused (`bridge_client.cpp:56-61`); OpenSSL backend pinned + revalidated (`bridge_global.cpp:103-114`); proxy env locked out, explicit proxy required (`bridge_request.cpp:571-577`, `bridge_multi.cpp:363-368`); https→http redirect blocked (`:159`); UNRESTRICTED_AUTH=0 (auth/cookie stripped cross-origin); NativeLibraryLoader absolute-only, no PATH/cwd, first-path-wins (`NativeLibraryLoader.cs:91-119`); `CURLHTTP_CA_BLOB` grants no new trust; decompression bomb bounded by consumer-paced queue; cookies purged on reuse (`bridge_client.cpp:175-180`); no unbounded EventSource retention.

### A1 — GCHandle / callback lifetime (reported)

| ID | Sev | Status | Claim | Key evidence |
|---|---|---|---|---|
| A1-1 | **Critical** | CONFIRMED (= seed G, sharpened) | Pre-header multi failure: loop-thread `OnMultiFinished` races SendAsync-continuation `OnWorkerFinished` **with no happens-before edge** → unfenced double `GCHandle.Free` (can free a *recycled* live handle → cross-request callback corruption) + double `ArrayPool.Return(_uploadBuffer)` (cross-request data corruption, seekable-body path) + slot/origin leak when `OnMultiFinished` throws before `OnFinished` (swallowed in `OnComplete` catch → admission deadlock under sustained failures) | loop: `bridge_multi.cpp:187-190`/`79-89`; `CurlRequestContext.cs:456-468,512,283-297`; TP: `CurlMultiDispatcher.cs:118`, `CurlHttpMessageHandler.cs:169-176`; guards `:287-295` unfenced check-then-act |
| A1-2 | High | CONFIRMED (= seed I) | Post-submit `MultiCancel` on disposed request SafeHandle (fast completion ran `OnFinished` first) → ODE at marshalling → `finally` double-releases `_slots`+origin (`SemaphoreFullException` when idle, else silent capacity inflation breaking `MaxConcurrentRequests`) | `CurlMultiDispatcher.cs:102,109-112,114-116,121-132`; window between submit and flag-clears |
| A1-3 | Medium | CONFIRMED (= seed H, managed+native) | `MultiSubmit` OK while `multi_destroy` shutting down → Submit lands after final drain → `on_complete` never fires → `await ResponseTask` hangs forever (no token on that await) + GCHandle/slot/`_active` leak | `CurlMultiDispatcher.cs:57,118`; `bridge_multi.cpp:195-199,221-224,252,263-269,377` |
| A1-4 | Low | CONFIRMED | `MultiSubmit` non-OK (or throw after `EnableMultiMode`): pump for non-seekable body never `Abort`ed → blocks forever in `Monitor.Wait` pinning TP task + 64KB rent + body stream; `_active` entry leaks (only removed in `OnFinished`) | `CurlRequestContext.cs:217-222,285`; `CurlMultiDispatcher.cs:88,93,104-107` |

**A1 refuted (guards confirmed — high documentation value):** worker engine has NO double-cleanup (DispatchAsync doesn't await ResponseTask → `dispatched=true` before any fault, `CurlRequestDispatcher.cs:82-84`); **pooled easy is `curl_easy_reset` at RELEASE not acquire** → no released-but-unreset window, clears callbacks+CURLOPT_PRIVATE (`bridge_client.cpp:169-193`); stale Unpause/Cancel commands gated by loop-thread-only `active` set + FIFO ordering → no freed-pointer deref/aliasing; seek-cb only fires inside perform (handle freed after); `RequestCancel` vs handle Dispose = swallowed ODE not native UAF (SafeHandle refcount + `in_send` spin backstop `bridge_request.cpp:400-409`); GCHandle Alloc/Free balanced on every exception path except A1-1 double + A1-3 hang; `AllocateSelfHandle` exactly once (no retry path); no double `on_complete` in any `start_request` failure branch (insert-after-configure + idempotent `active.erase`); stale `CURLMSG_DONE` for recycled easy killed by reset-nulls-PRIVATE + libcurl `remove_handle` message purge (**worth a code comment — safety rests on libcurl purge**); `SafeHandle.Dispose`/`CTR.Dispose`/`BodyQueue.Dispose` all idempotent+thread-safe (benign doubles); deleting native request inside `on_complete` not a UAF (last statement touching request).

### A4 — buffer / pool ownership + queue protocol (reported)

| ID | Sev | Status | Claim | Key evidence |
|---|---|---|---|---|
| A4-1 | Medium | CONFIRMED | Overlapped 2nd `ReadAsync` silently overwrites `_pendingReadTcs` → first reader hangs forever AND its cancellation is a no-op (`ReferenceEquals` fails) → uncancellable hang; single-consumer contract asserted not enforced | `BoundedByteQueue.cs:210-214,231,240,244`. Fix: throw under lock if `_pendingReadTcs is not null` |
| A4-2 | Low | CONFIRMED | `ReadBodyDirect` grow path returns `_uploadBuffer` before nulling field; if `Rent` throws (OOM), field still points at returned array → double-Return at cleanup → ArrayPool corruption. (Grow path ~dead: curl asks constant size) | `CurlRequestContext.cs:379-386` vs `:562-566`/`:291-295`. Fix: null-before-return reorder |
| A4-3 | Low (harden) | CONFIRMED (seed J downgraded) | Seed J does NOT hang: `OnBodyData` checks `_cancelRequested` first; every `Abort` caller escorted by `MultiCancel`; pause ≤ one loop iteration. Residual: `TryWrite` conflates full/aborted → returns 2 (pause) not 1 (abort); correctness rests on unverifiable escort invariant | `CurlRequestContext.cs:305,141-154`; `BoundedByteQueue.cs:314-317,342-343`; `bridge_multi.cpp:207-212,263-267`. Fix: re-check `_cancelRequested` after false TryWrite, or tri-state |
| A4-4 | Low (harden) | CONFIRMED | Upload side: `TryReadUpload` never observes `_aborted`; `Abort` doesn't fire data-available wake → aborted upload queue answers "pause forever", liveness relies on `MultiCancel` escort | `BoundedByteQueue.cs:351-374,167-181`. Fix: surface abort from `TryReadUpload` like sync `Read` does |
| A4-5 | Low (doc) | CONFIRMED | Pump leaks 1 Task + 64KB rent + body stream if content stream ignores cancellation (only `Write`-block is unblockable, not `ReadAsync`); bounded, non-cumulative | `CurlRequestContext.cs:254-255,221`. Doc in limitations |
| A4-6 | Low (doc) | CONFIRMED | True per-request bound = `MaxResponseBufferBytes + ReceiveBufferSize` (+ ~2× ArrayPool rounding), not `MaxResponseBufferBytes`; with 64KB cap + 10MB recv buf actual = ~10MB (160×). Upload ≈256KB default | `BoundedByteQueue.cs:120,333,407-410`; `CurlHttpMessageHandler.cs:369`. Doc the bound |
| A4-8 | Low (test gap) | CONFIRMED | Zero unit coverage of `TryWrite`/`TryReadUpload`/pause-unpause callbacks/sync `Read` — why A4-3/4 latent | `BoundedByteQueueTests.cs` (all Write/ReadAsync only) |

**A4 refuted (guards confirmed):** seed E fully handled — pump `_bodyReadCts?.Token` ODE evaluated inside try, bypasses OCE catch → generic catch → `Fault` (no-op after Abort) → buffer returned; `_uploadPump` unobserved is benign (pump can't complete faulted); catch order correct. Segment double-return impossible (pop+return atomic under `_sync`); Abort+Dispose double-drain empty (write-refusal-after-abort); rendezvous claim protocol airtight (claim-by-null under lock, `TrySetCanceled` sole claimant); producer-wins-cancellation covered by existing test; sync `Read` wakes on every relevant state change (rendezvous-tail no-pulse requires forbidden 2-consumer mix = A4-1); `CurlResponseStream` read-after-dispose/concurrent-dispose safe; **header bytes never transit pooled arrays** (Latin1→string / CoTaskMem UTF-8) — only body bytes do. Q10 zeroing stance: keep hot paths un-cleared (BCL precedent), consider clearing cold paths (DrainLocked + `_uploadBuffer`) + doc.

### A3 — native bridge (reported)

| ID | Sev | Status | Claim | Key evidence / fix |
|---|---|---|---|---|
| A3-1 | High (OOM-gated) | CONFIRMED (= seed A) | 4 multi exports (`multi_submit/cancel/unpause_write/unpause_read`) no try/catch; `enqueue` `push_back` + `last_error=` string allocs throw `bad_alloc` → SEH across LibraryImport → crash. Violates `curl_bridge.h:14-15` | `bridge_multi.cpp:59-69,351-407`. Fix: `try{}catch(...)` per body |
| A3-2 | High (OOM-gated) | CONFIRMED (new) | Loop thread has ZERO exception containment — `std::thread` lambda + `run()` unguarded; `active.insert`/`describe_failure`/`effective_url=`/vector-ctor throw → `std::terminate` kills process | `bridge_multi.cpp:300,123,148,156,264`. Fix: per-command try/catch + whole-`run()` guard that still runs shutdown sweep |
| A3-3 | Medium | CONFIRMED (new) | catch handlers themselves allocate (`std::string("...")+ex.what()`) → `bad_alloc` while handling `bad_alloc` escapes → same crash | `bridge_request.cpp:660-665`, `bridge_client.cpp:266-269`, `bridge_multi.cpp:303-306`. Fix: no-alloc catch bodies |
| A3-4 | Medium | CONFIRMED (= seed H native) | Submit enqueued after final command swap never completed → hang + leaks. **Currently unreachable** via managed (SafeHandle refcount + FIFO orders Submit-push before Shutdown-push) but rests entirely on that; native has no self-defense | `bridge_multi.cpp:195-199,221-223,263-269,378`. Fix: final drain fails queued Submits CANCELLED + `accepting` atomic |
| A3-5 | Low (latent) | CONFIRMED | `finish_request` idempotence guard gates only `remove_handle`, NOT `on_complete` (fires unconditionally) — safe today (4 paths proven single-shot) but a booby trap; also add-fail calls `remove_handle` on never-added easy (benign `CURLM_BAD_EASY_HANDLE`) | `bridge_multi.cpp:134-136,161-190`. Fix: explicit `request->finished` bool guard; insert into `active` only after `add_handle` succeeds |
| A3-6 | Low | CONFIRMED | Stale queued Cancel holds raw `curl_bridge_request*`; pointer-reuse ABA can cancel an unrelated new request at the same address (gates prevent deref but not spurious cancel) | `bridge_multi.cpp:381-389,208-216`. Fix: per-request generation number in Command, or purge commands in `finish_request` under `cmd_mutex` |
| A3-7 | Medium | CONFIRMED (= seed C) | int64→`long`(32-bit LLP64) truncation of CONNECTTIMEOUT_MS/TIMEOUT_MS/MAXAGE_CONN/MAXLIFETIME_CONN; unbounded managed-side. `RequestTimeout=FromDays(30)`→negative→every request INTERNAL_ERROR; ≥2^32→silently-small timeout | `bridge_request.cpp:200-220`; `CurlHttpClientOptions.cs:168-202` no bounds. Fix: native clamp `[0,LONG_MAX]` + managed Validate |
| A3-8 | Low | CONFIRMED | `CURLOPT_MAXREDIRS` set unconditionally; `Validate()` checks `MaxAutomaticRedirections>=1` only when `AllowAutoRedirect=true` → `-5` with redirects off → every request INTERNAL_ERROR | `bridge_request.cpp:179`; `CurlHttpClientOptions.cs:177-180`. Fix: validate range regardless, or only set when following |
| A3-9 | Info/Low | CONFIRMED | Read callback returns `static_cast<size_t>(produced)` without `produced <= size*nitems` check — relies on libcurl's internal guard (defense-in-depth gap) | `bridge_callbacks.cpp:151-163`. Fix: one-line upper-bound → ABORT |
| A3-10 | Low | CONFIRMED | `~curl_bridge_request` frees `headers` not standalone `extra_headers` — only leaks via A3-4 orphan path (else extra is linked into headers tail) | `bridge_internal.h:110-116`. Fix: detach-then-free in dtor |
| A3-11 | Info | CONFIRMED | `finish_request` unconditionally overwrites precise configure-fail `last_error` with generic "Failed initialization" → diagnostics lost | `bridge_multi.cpp:153-157`. Fix: overwrite only when empty |

**A3 refuted (guards confirmed):** **seed K refuted** — pool grows on demand (`curl_easy_init` when `free_handles` empty, `bridge_client.cpp:136-167`); `MaxEasyHandles` is only a pre-alloc hint (never shrinks below peak — footnote); status-line parse capped at 3 digits (no overflow); no double `on_complete` (disjoint failure families); `size*nmemb` can't overflow (libcurl caps + `checked((int)`); extra_headers detach handles all reachable states (null/one-node/tail); stale CURLMSG_DONE killed by `remove_handle` message-purge (libcurl, residual doubt noted) + PRIVATE-reset backstop; `get_last_global_error` same-thread immediate copy; POD setters can't throw; **Q12 ABI verified field-by-field clean** (offsets/widths/enum-width/cdecl/struct_size-gating all agree); **process-exit safe** (.NET doesn't await finalizers at exit; no DllMain; loop-thread callbacks non-blocking by construction — `TryWrite`/PAUSE + RunContinuationsAsynchronously); share-lock array in-bounds, DNS+SSL_SESSION shared, CONNECT correctly absent; no-`curl_global_cleanup` intentional (static-CRT, document).

### A2 — shutdown / dispose / cancel races (reported)

| ID | Sev | Status | Claim | Key evidence / fix |
|---|---|---|---|---|
| A2-1 | **Critical** | CONFIRMED (new, WORKER engine) | `Dispose` sets `_disposed` → `WorkerLoop while(!_disposed)` exits without draining `_work`; queued-but-undispatched contexts' `_responseTcs` never completed (`Cancel` doesn't touch TCS) → `SendAsync` hangs forever, no token rescues (no token on `await ResponseTask`) | `CurlRequestDispatcher.cs:135,233-239`; `CurlRequestContext.cs:139-169`; `CurlHttpMessageHandler.cs:166`. Fix: drain `_work` after join, `TryFailFastIfCancelled` each remainder |
| A2-2 | High | CONFIRMED (new, WORKER) | `Join(10s)` times out (uncancellable body) → `_slots.Dispose()`; straggler worker later `OnFinished`→`_slots.Release()` throws ODE from `RunRequest` finally (not covered by its catch) → unhandled on manual thread → **process crash** | `CurlRequestDispatcher.cs:246-253,77,206,155`. Fix: A2-1 drain + tolerant release / don't dispose slots while workers may run |
| A2-3 | High | CONFIRMED (new, WORKER) | After Join timeout, `client_destroy` yield-spins on `active_requests>0` with NO timeout → disposing thread never returns, 100% core | `bridge_client.cpp:278-317,292-295`; `bridge_request.cpp:591,656-657`. Fix: bounded spin or ensure workers truly stopped first |
| A2-4 | Medium | CONFIRMED (= seed B, corrected) | Multi post-dispose completion → `OnFinished` `_slots.Release()` on disposed semaphore throws ODE, swallowed in `OnComplete catch{}`. **Correction: request handle NOT leaked** (`owned.Dispose()` runs before Release); only origin-gate release skipped; parked admission waiter fails ≤30s late with masked ODE | `CurlMultiDispatcher.cs:155-174,94-100`; `NativeCallbacks.cs:100-111`. Fix: drain-before-dispose + tolerant release |
| A2-5 | High (cond. `MaxConnectionsPerServer>0`, default 0) | CONFIRMED (new, both engines) | `originGate.WaitAsync(token)` no timeout; `SemaphoreSlim.Dispose` doesn't wake async waiters + permits never arrive (ODE skips gate release) → with `CancellationToken.None` `SendAsync` hangs forever | `CurlMultiDispatcher.cs:46,170-173`; `CurlRequestDispatcher.cs:58,254-257`. Fix: complete parked waiters on dispose |
| A2-6 | High impact / low likelihood | CONFIRMED (extends A1-2/I) | Post-submit `MultiCancel` ODE (concurrent dispose): `finally` double-releases slot AND `request?.Dispose()`→`delete request` while loop still owns the queued Submit pointer → **native use-after-free** + `OnWorkerFinished` frees recycled GCHandle → misdirected completion | `CurlMultiDispatcher.cs:102,111,114-116,122-131`; `bridge_request.cpp:410`. Fix: move `CancelRequested` backstop after handoff + try/catch, or rely on native `start_request` cancel check |
| A2-7 | Low-Med | CONFIRMED (= A1-4) | Pre-submit failure after `EnableMultiMode`: `_active` entry leaks (removed only in `OnFinished`) + pump wedged forever (only `OnMultiFinished` aborts `_uploadQueue`, but `!dispatched` path runs `OnWorkerFinished`) | `CurlMultiDispatcher.cs:88,93,105`; `CurlRequestContext.cs:285,555-571` |
| A2-8 | Low | CONFIRMED (new) | Non-atomic `volatile bool` dispose guards (handler + both dispatchers) allow concurrent double-entry; double `BlockingCollection.CompleteAdding`/`SafeHandle.Dispose` not contract-guaranteed safe | `CurlHttpMessageHandler.cs:406-408`; `CurlRequestDispatcher.cs:229-233`; `CurlMultiDispatcher.cs:157-161`. Fix: `Interlocked.Exchange` |

**A2 refuted (guards confirmed):** **seed H refuted (R1)** — SafeHandle `ReleaseHandle` deferred until all in-flight P/Invokes' AddRef released → any native-executing `MultiSubmit` enqueued its Submit strictly before Shutdown (FIFO) → processed + drained CANCELLED; if handle already closed, `MultiSubmit` throws ODE pre-native → `!dispatched` frees GCHandle (no leak/hang). **Seed F/Q8 refuted** — both engines post the SAME `RunContinuationsAsynchronously` headers-continuation (no worker/multi asymmetry); streaming is pool-free on both (producer thread pulses `Monitor` directly; consumer unpause is direct P/Invoke); no wait cycle → degraded latency under starvation, never deadlock. Native double-`on_complete` refuted (R2, one libcurl msg-purge assumption); cancel-command ABA refuted (R3, FIFO); sweep-missed-context bounded not missed (R4, `multi_cancel` sets flag before enqueue + `start_request` checks first / destroy-drain terminates); triple-Cancel tearing refuted (R5, idempotent Abort + ODE-guards + `_multiRequestHandle` never nulled).

### A5 — async hygiene / bounds / lock-order / parity (reported)

**LOCK-ORDER GRAPH PROVEN ACYCLIC** — all cross-domain edges managed→native (`_sync`→`cmd_mutex` via unpause callback fired under `_sync`; `_sync`→`Loader.Sync`); every native→managed transition (`OnComplete`→`_sync`) occurs with **zero native locks held** (`process_commands` swaps deque under `cmd_mutex` then releases before executing; `finish_request` calls `release_handle` before `on_complete`); `cmd_mutex`/`pool_mutex`/`share_locks`/`_threadSync` all leaves. Both TCS sites are `RunContinuationsAsynchronously` → no user continuation runs inline under `_sync`. **No lock-order deadlock exists.**

| ID | Sev | Status | Claim | Key evidence / fix |
|---|---|---|---|---|
| A5-1 | High | CONFIRMED (= A2-2) | Straggler worker ODE from `_slots.Release()` in unguarded `RunRequest` finally → process crash (worker engine only; multi is fenced by `OnComplete catch`) | `CurlRequestDispatcher.cs:210-214,155,249` |
| A5-2 | Medium | CONFIRMED (= A4-5/A2-7 refined) | `OnMultiFinished` disposes `_bodyReadCts` **without cancelling** → pump parked in `stream.ReadAsync` never interrupted (queue Abort only wakes `Write`-block) → pump+64KB+stream+context rooted | `CurlRequestContext.cs:283-297,254-259`. **Fix: `_bodyReadCts?.Cancel()` before Dispose** |
| A5-3 | Medium | CONFIRMED (= A1-4/A2-7) | Pre-submit failure: `_uploadQueue` never aborted (`!dispatched`→`OnWorkerFinished`, not `OnMultiFinished`) → pump ThreadPool thread parked forever for body ≥128KB | `CurlMultiDispatcher.cs:88,102-107`; `CurlRequestContext.cs:688-692` |
| A5-4 | Low-Med | CONFIRMED (new) | `_multiMode`/`_multiClient`/`_multiRequestHandle` plain (non-volatile); `Cancel()` stale-reads `_multiMode==false` → worker `RequestCancel` path (flag only, no wakeup) → **paused** download transfer never observes cancel until dispose (ARM64-plausible) | `CurlRequestContext.cs:43-45,151,231-232`. Fix: volatile, or always `MultiCancel` when `_multiClient!=null` |
| A5-5 | Low | CONFIRMED (new) | Missing `ConfigureAwait(false)` on `await using` in `AwaitPendingReadAsync` — the ONLY context-capturing await in `src`; UI/ASP-classic sync-blocked read + concurrent token race → deadlock | `BoundedByteQueue.cs:225-246`. Fix: `.ConfigureAwait(false)` |
| A5-6 | Medium | CONFIRMED (new) | `EnsureWorkerAvailable` idle-check `_idleThreads>0` TOCTOU: two dispatches see the one idle worker, both skip creation; item 2 stranded behind a long streaming item 1 despite `_threads.Count<max` | `CurlRequestDispatcher.cs:111-116,139,152`. Fix: create when `_work.Count>idle` under `_threadSync` below cap |
| A5-7 | Low (design) | CONFIRMED (= A2-5 doc angle) | Origin gate acquired before global slot with NO timeout on origin wait → same-origin head-of-line starvation + bypasses documented fail-fast; order is consistent (no deadlock) | `CurlRequestDispatcher.cs:56-62`; `CurlMultiDispatcher.cs:44-50`. Fix: timeout on origin wait or document |
| A5-8 | Low (doc) | CONFIRMED | Worker threads never retired (parked forever, ~1MB stack each up to cap); `_perOrigin` never evicted (1 SemaphoreSlim/origin, ~100MB for 1M hosts) | `CurlRequestDispatcher.cs:141,36,99-109`. Document |
| A5-9 | Low | CONFIRMED (= A4-4) | `TryReadUpload` never checks `_aborted` → aborted upload queue reads as would-block (pause forever); masked today by cancel-escort | `BoundedByteQueue.cs:351-374,167-181` |
| A5-10 | Low (test) | CONFIRMED | `SyncSend_UnderThreadPoolPressure_DoesNotDeadlock` proves little: gated, 45s allowance, worker-engine-only (ignores `CURLHTTP_ENGINE`), no streaming/upload | `StressAndResourceTests.cs:202-234` |

**A5 refuted (guards confirmed):** `_work` bounded ≤ `MaxConcurrentRequests` (slot-gated single Add site); **command-storm refuted** — edge-triggered `_producerPaused`/`_consumerWaitingUpload`, ≤1 outstanding unpause/direction/request, 8MB download ≈8 unpauses not 32/read; cancel-during-setup refuted (3 guards: `multi_cancel` sets flag before enqueue + `start_request` checks first + FIFO + post-submit re-check); no user code under `_sync` (both TCS = RCA); origin-gate token honored both waits; **HttpClient.Timeout shape correct** (caller token carried → HttpClient rewraps TCE+TimeoutException inner; `CURLE_OPERATION_TIMEDOUT`→TCE+TimeoutException); ValueTask consumed exactly once each; multi loop thread joined before Dispose returns.

---

## Consolidated master list (deduplicated, for S2 verification → S3 fixes)

Cross-agent dedup. **Reachability reconciliation:** seed H / A3-4 / A1-3 — native contract hole but **REFUTED as reachable via managed API** (A2-R1 + A3 Q-analysis: SafeHandle ReleaseHandle deferral + FIFO). Keep as native hardening, downgrade to "latent — unreachable today". Seed F — **REFUTED as deadlock** (A2 Q8 + A5-10: degraded latency only). Seed J — **REFUTED as hang** (A4-3: bounded pause, escorted). Seed E — **REFUTED** (A4: pump catch handles ODE). Seed K — **REFUTED** (A3: pool grows on demand).

| # | Sev | Cluster | Merged from | Fix home |
|---|---|---|---|---|
| M1 | **Critical** | Multi failed-request dual-cleanup data race (double GCHandle.Free w/ recycling corruption + double ArrayPool.Return + slot-leak-on-throw) | G, A1-1, A4-7 | S3.2 single-owner Interlocked cleanup claim |
| M2 | **Critical** | Worker Dispose orphans queued requests → uncancellable hang | A2-1 | S3.3 drain `_work` + fail remainder |
| M3 | High | Worker straggler ODE → process crash | A2-2, A5-1 | S3.3 tolerant release + guard finally |
| M4 | High | Worker `client_destroy` unbounded spin after Join timeout | A2-3 | S3.3 (couple with M2/M3: ensure workers stopped) |
| M5 | High | Origin-gate parked waiter hangs forever on dispose (`MaxConnectionsPerServer>0`) | A2-5, A5-7 | S3.3 complete parked waiters / timeout |
| M6 | High | Post-submit MultiCancel ODE → double slot-release + **native UAF** (`request?.Dispose` on loop-owned req) | I, A1-2, A2-6 | S3.3 move backstop after handoff + try/catch |
| M7 | High (OOM) | 4 multi exports throw across C ABI | A, A3-1 | S3.1 noexcept+catch |
| M8 | High (OOM) | Loop thread unguarded → `std::terminate` | A3-2 | S3.1 per-command + run() guard |
| M9 | Medium | Allocating catch handlers rethrow across ABI | A3-3 | S3.1 no-alloc catch |
| M10 | Medium | int64→long timeout truncation, unbounded managed | C, A3-7 | S3.1 native clamp + S3.5 Validate |
| M11 | Medium | Multi post-dispose ODE swallowed (masking, poor diag; NOT a leak) | B, A2-4 | S3.3 drain-before-dispose |
| M12 | Medium | Multi pump orphan on completion (`_bodyReadCts` disposed not cancelled) | E(refuted-as-crash)/A5-2, A4-5 | S3.4 Cancel before Dispose |
| M13 | Medium | Pre-submit pump leak + `_active` leak | A1-4, A2-7, A5-3 | S3.4 abort `_uploadQueue` on `!dispatched` |
| M14 | Medium | Overlapped ReadAsync → uncancellable hang (single-consumer unenforced) | A4-1 | S3.4 throw under lock |
| M15 | Medium | Worker idle-check TOCTOU strands request | A5-6 | S3.3 create when queued>idle |
| M16 | Medium | Response header count/size unbounded (DoS) | A6-1 | S3.5 MaxResponseHeaders* option |
| M17 | Medium | No CRLF validation on request header values | A6-2 | S3.5 validate in FormatHeaderLine |
| M18 | Medium | Proxy username leaks via verbose TEXT lines | A6-3 | S3.5 redact TEXT auth notices |
| M19 | Low | abort/pause conflation both queue directions (fragile, escorted today) | J/A4-3, A4-4, A5-9 | S3.4 recheck cancel / surface abort |
| M20 | Low | `ReadBodyDirect` grow path return-before-rent double-return | A4-2 | S3.4 null-before-return |
| M21 | Low | Native `finish_request` on_complete not gated by idempotence + insert-before-add | A3-5 | S3.1 `finished` bool + insert-after-add |
| M22 | Low | Stale Cancel pointer ABA | A3-6 | S3.1 generation number (or accept+comment) |
| M23 | Low | MAXREDIRS unvalidated when redirects off | A3-8 | S3.5 Validate |
| M24 | Low | Read-callback `produced` upper-bound (defense-in-depth) | A3-9, D | S3.1 one-line clamp |
| M25 | Low | dtor doesn't free standalone extra_headers (only via M-orphan) | A3-10 | S3.1 |
| M26 | Low | `_multiMode` non-volatile stale cancel routing | A5-4 | S3.4 volatile |
| M27 | Low | Missing ConfigureAwait(false) in AwaitPendingReadAsync | A5-5 | S3.4 |
| M28 | Low | Non-atomic dispose guards (double-entry) | A2-8 | S3.3 Interlocked |
| M29 | Low | configure error message clobbered by finish_request | A3-11 | S3.1 |
| M30 | Low/doc | True mem bound = MaxResponseBufferBytes+ReceiveBufferSize; pump-leak on uncoop stream; threads/perOrigin growth; SECLEVEL cipher; CA live-path tamper; key-pw lifetime; URL userinfo redaction; pooled-array non-zeroing | A4-6, A4-5, A5-8, A6-4/5/6/url, A4-Q10 | S3.5 docs + selective |
| M31 | Low (test) | SyncSend starvation test weak; queue TryWrite/TryReadUpload/sync-Read uncovered | A5-10, A4-8 | S4 tests |
| M32 | Medium | CONFIRMED during S3.3 validation: `_callerRegistration.Dispose()` blocks until a concurrent cancel callback finishes → intermittent completion-vs-cancel stall under load (reproduced in `CancellationAndDisposal_Simultaneously_NeverHang` on the multi engine under full-suite ThreadPool pressure). Fixed with `Unregister()` (non-blocking; `Cancel()` is idempotent). Same pattern SocketsHttpHandler uses. `CurlRequestContext.cs` ReleasePerRequestResources | Fixed S3.3 |

## Refuted hypotheses (consolidated — documentation value)

Seed K (pool grows on demand); seed F (no deadlock, degraded latency); seed J (bounded escorted pause); seed E (pump catch handles ODE); seed H (unreachable via managed — SafeHandle FIFO). Plus per-agent refutations recorded in A1–A6 sections above: pooled-easy reset-at-release, idempotent finish (4 paths), FIFO command staleness, ABI layout field-by-field clean, process-exit/finalizer safe, lock-order acyclic, verification-always-on, no cert-bypass, command-storm edge-triggered, HttpClient.Timeout shape correct.

## Refuted hypotheses

_(consolidated with the exact guard that kills each — documentation value; see per-agent sections above)_
