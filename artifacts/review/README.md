# Deep production-readiness review

Adversarial memory-safety / concurrency / security review of CurlHttpClient
(managed `HttpMessageHandler` over a native libcurl + OpenSSL bridge, two
execution engines). Method: 6 parallel read-only review agents → refute-first
verification → fixes in dependency order, one commit per cluster, full suite
green on both engines after each.

## Read this first

- **[verdict.md](verdict.md)** — the release recommendation, what changed, and
  residual risks.
- **[qa-answers.md](qa-answers.md)** — the required question set, answered.
- **[findings.md](findings.md)** — every finding: evidence chain, severity,
  status, and resolution. Includes the consolidated master list (M1–M32) and
  the refuted-hypotheses record.

## Outcome

| | Count |
|---|---|
| Findings fixed | 32 (incl. M32 found during validation) |
| Critical | 2 (M1 corruption/hang, M2 worker dispose hang) |
| High | 6 (2 process-crash paths, native UAF, hangs) |
| Refuted / not-a-bug (documented) | seeds F/H/J/K/E + many per-agent |
| New tests | 11 unit + 6 integration (both engines) |
| Final suite | unit 74; integration 333 ×2 engines; gated stress ×2; ABI + import gate |
| Perf regression | none (both engines vs optimized baseline) |

## Deliverables map

The review spec's 24 deliverables, and where each lives:

| # | Deliverable | Location |
|---|---|---|
| 1 | Environment/compatibility report | qa-answers (Language/build); findings.md header |
| 2 | Language-version recommendation | qa-answers; commit S6 |
| 3 | C++ standard recommendation | qa-answers (C++17) |
| 4 | Ownership/lifetime | qa-answers (Memory/lifetime); findings A1/A4 |
| 5 | Threading model | findings A5 (lock-order graph) |
| 6 | State machine | finish_request `finished` flag (S3.1); ReleasePerRequestResources |
| 7 | Lock/synchronization inventory | findings A5 |
| 8 | Lock-order diagram | findings A5 (proven acyclic) |
| 9 | Managed memory-leak report | qa-answers; findings A1/A2/A4 |
| 10 | Native memory-leak report | qa-answers; findings A3; `/analyze` clean |
| 11 | Callback-lifetime report | qa-answers; findings A1 |
| 12 | Buffer-ownership report | findings A4 (ownership table) |
| 13 | SafeHandle/GCHandle audit | qa-answers; findings A1 |
| 14 | P/Invoke ABI audit | findings A3 (Q12 field-by-field) |
| 15 | Security audit | findings A6; docs/security-review (M18/M30 updates) |
| 16 | Analyzer/warning report | commit S6; `/analyze` + `/W4 /WX` clean |
| 17 | Prioritized findings | findings.md master list |
| 18 | Implemented fixes | commits S3.1–S3.5 |
| 19 | Added tests | commit S4; ReviewFixTests / ReviewRegressionTests |
| 20 | Stress/race results | qa-answers; gated stress ×2 engines green |
| 21 | Before/after performance | perf harness (no regression) |
| 22 | WS2012R2 validation | verdict (residual risk 1); import gate + ABI test green |
| 23 | Known limitations | docs/limitations.md; verdict residual risks |
| 24 | Release recommendation | verdict.md |

## Commit trail

- `Review S3.1` — native ABI exception-safety + hardening
- `Review S3.2` — single-owner request cleanup (Critical M1 + M6)
- `Review S3.3` — dispatcher shutdown/dispose/cancellation hardening (Critical M2 + Highs)
- `Review S3.4` — streaming queue + upload buffer ownership
- `Review S3.5` — options validation, header-flood cap, redaction
- `Review S4` — regression tests
- `Review S5` — native static-analysis + ASan build options
- `Review S6` — language-version pin, .editorconfig, native /WX
