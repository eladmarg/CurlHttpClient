# Deep review — production-readiness verdict

## Recommendation: RELEASE (with the documented WS2012R2 on-target gate)

The adversarial review found and fixed real defects — including two Critical
uncancellable-hang / memory-corruption bugs and two process-crash paths that
the existing 376-test certification suite did not catch — and every fix is
covered by a new regression test and validated on both execution engines. The
library is safe to release for its intended use (a long-lived, shared handler
in a service client), subject to the on-target validation checklist for actual
Windows Server 2012 R2 deployment.

## What the review changed

31 findings from the fan-out plus 1 found during fix validation (M32), fixed
across six commits (S3.1–S3.5, S4). Headlines:

- **M1 (Critical)** — the multi engine double-freed a request's GCHandle (an
  unfenced race that could free a slot already reused by another live request →
  cross-request callback corruption) and double-returned a pooled buffer on any
  pre-header failure. Fixed by making the loop thread the single cleanup owner
  and an Interlocked once-guard.
- **M2 (Critical)** — disposing the handler on the default worker engine
  orphaned queued requests, hanging their `SendAsync` forever with no
  cancellation escape. Fixed by draining the queue and failing orphans.
- **M3 / M8 (process crashes)** — a straggler worker's release after the join
  timeout threw an unhandled exception on a dedicated thread; the native
  curl_multi loop thread had zero exception containment (`std::terminate`).
  Fixed by not disposing the semaphores + tolerant release, and per-command +
  whole-loop native exception guards.
- **M6 (native use-after-free)**, **M5 (origin-gate hang)**, **M32
  (cancellation stall)**, and the native ABI exception-safety, timeout
  truncation, header-flood DoS, and proxy-username log-leak items.

## What was refuted (not bugs — documented for confidence)

Certificate/hostname verification cannot be disabled; the lock-order graph is
acyclic (no lock-ordering deadlock); the ABI layout is field-by-field correct;
the process-exit/finalizer path is safe; the easy-handle pool grows on demand
(no exhaustion); sync `Send` degrades but never deadlocks; the connection pool,
share handle, and command-queue staleness are all sound.

## Residual risks (accepted / documented)

1. **No physical WS2012R2 run.** Mitigated by the import gate on the rebuilt
   `/WX` DLL (Windows 8.1-floor imports only, static CRT) and the native ABI
   test. The on-target checklist in docs/deployment-ws2012r2.md is the release
   gate for a real deployment. **This is the one item a release owner must
   close on target hardware.**
2. **.NET 10 is unsupported by Microsoft on WS2012R2** (standing, accepted
   project decision; net8.0 retarget documented as a fallback).
3. **A wedged, cancellation-ignoring request body** can make `Dispose` wait on
   the native `active_requests` backstop rather than return promptly — correct
   behavior (the alternative is a use-after-free), documented.
4. **Caller-supplied cipher strings** may include `@SECLEVEL` (A6-5), and a
   caller may put credentials in a request URL's userinfo (A6-url) — both
   documented; neither disables verification.
5. **h2 upstream pause-buffering** can exceed `MaxResponseBufferBytes` by up to
   one stream window on the multi engine (pre-existing, documented).
6. **Analyzer-as-error rollout deferred** — a mechanical follow-up, posture
   recorded in .editorconfig; does not affect runtime safety.

## Evidence

- findings.md — every finding, its evidence chain, and resolution.
- qa-answers.md — the required question set answered with references.
- Test suite: unit 74; integration 333 × 2 engines; gated stress × 2 engines;
  native ABI test; WS2012R2 import gate; consumer offline suite.
- No performance regression from the safety fixes (perf harness, both engines,
  vs the committed optimized baseline).
- `/analyze` clean; native `/W4 /WX` clean.
