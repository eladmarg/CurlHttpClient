# P5 — buffer-size experiments (balanced)

Swept the three tunable buffers against the harness scenarios (loopback,
warm pool). Goal: find a better *balanced* default, change one only if it does
not regress the other profile.

## Receive buffer (CURLOPT_BUFFERSIZE) — download + small GET

| ReceiveBufferSize | small-GET median µs | 32 MB download median µs |
| --- | ---: | ---: |
| 64 KB | 188.7 | 32,582 |
| **256 KB (default)** | 227.2 | 32,415 |
| 512 KB | 191.6 | 33,165 |

Download throughput is flat within run-to-run noise across all three sizes;
small-GET likewise. No size wins. **Keep 256 KB.**

## Upload buffer (CURLOPT_UPLOAD_BUFFERSIZE) — 8 MB upload

| UploadBufferSize | 8 MB upload median µs |
| --- | ---: |
| **64 KB (libcurl default)** | 7,948 |
| 1 MB | 32,343 |

Raising the upload buffer to 1 MB **regressed upload ~4×**: libcurl issues
fewer but much larger `send()` calls, which interacts poorly with the socket
send buffer / cadence on loopback. **Keep the 64 KB default**; the
`UploadBufferSize` option remains available for workloads that measure a
benefit (e.g. high-bandwidth-delay-product links).

## Decision

No default changed. The experiments confirmed the current defaults are the
balanced choice and ruled out a plausible-but-wrong change (large upload
buffer). `ReceiveBufferSize` and `UploadBufferSize` stay exposed as knobs.
