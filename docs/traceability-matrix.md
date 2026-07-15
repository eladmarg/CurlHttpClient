# Requirements → tests traceability

Each requirement from the test brief maps to the test(s) that satisfy it.
Folder paths are under `tests/CurlHttpClient.IntegrationTests/` unless noted.

| # | Requirement | Test(s) | Evidence |
| --- | --- | --- | --- |
| 1 | Every applicable HttpClient request API | `ApiCoverage/*`, `Methods/ClientMethodOverloadTests`, `Json/JsonExtensionTests`, `SyncSend/SyncSendTests` | 100-API baseline, enforcing gate, 66 Direct tests |
| 2 | Standard HTTP methods | `Methods/ClientMethodOverloadTests`, `CustomVerbTests` | GET/HEAD/POST/PUT/PATCH/DELETE/OPTIONS/TRACE |
| 3 | Custom verbs | `Methods/CustomVerbTests` | PROPFIND/REPORT/MKCOL/LOCK/UNLOCK/custom + case preservation |
| 4 | Upload streams (no full buffering) | `Content/RequestContentTests.Upload_IsStreamedIncrementally…` | upload-buffering detector (first byte ≪ total) |
| 5 | Download streams (no full buffering) | `HttpBehaviorTests.ResponseHeadersRead_ReturnsBeforeBodyCompletes`, `Stress.LargeDownload_StaysWithinBoundedBuffer` | headers < body time; 100 MB ≤ 2 MB managed |
| 6 | Bounded backpressure | `Stress.LargeDownload_StaysWithinBoundedBuffer`, unit `BoundedByteQueueTests` | peak managed vs cap assertion |
| 7 | Cancellation in every phase | `CancellationAndTimeoutTests`, `Errors/ErrorMappingEdgeTests`, `Disposal/*` | pre-send/connect/TLS/headers/upload/download/backpressure/dispose |
| 8 | Timeout vs cancellation distinguishable | `CancellationAndTimeoutTests`, `CurlErrorMapperTests` | TaskCanceledException(+TimeoutException) vs OCE(token) |
| 9 | Header parsing / multiple blocks | `Headers/HeaderBehaviorTests`, `StatusCodes/StatusCodeTests` (100/103), unit `ResponseHeaderParserTests` | interim blocks, obs-fold, Set-Cookie split |
| 10 | Redirects per documented policy | `Redirects/RedirectMatrixTests` | per-status method transform, auth stripping, loop |
| 11 | Compression per packaged algorithm | `Compression/CompressionEdgeTests` | gzip/deflate/br; invalid/truncated/disabled |
| 12 | Proxy behavior | `Proxy/ProxyBehaviorTests` | forward/CONNECT/407/bypass/env-isolation/redaction |
| 13 | Connection reuse (server evidence) | `Pooling/ConnectionPoolingTests` | X-Connection-Id ids |
| 14 | Parallel requests no corruption | `Stress.ConcurrencyLadder…`, `HttpBehaviorTests.ParallelRequests…` | SHA-256 verified mixed workload |
| 15 | Runtime TLS backend is OpenSSL | `ApiCoverage/RuntimeDiagnosticsArtifactTests`, `HttpBehaviorTests.Diagnostics_ProveOpenSslBackend` | GetDiagnostics().TlsBackendIsOpenSsl |
| 16 | TLS 1.2 validated | `Tls/ProtocolVersionMatrixTests`, `CipherSuites/*` | pinned s_server negotiation |
| 17 | TLS 1.3 validated | `Tls/ProtocolVersionMatrixTests`, cipher matrix | 5 TLS1.3 suites negotiated |
| 18 | Exact packaged OpenSSL cipher inventory | `CipherSuites/CipherMatrixOrchestratorTests`, unit `CipherManifestTests` | native enumeration, 94 suites |
| 19 | Every cipher tested or classified | `CipherMatrixOrchestratorTests` | 64 passed, 30 NA, 0 silent skips |
| 20 | Applicable ciphers negotiate when forced | `CipherMatrixOrchestratorTests` | server-confirmed per suite |
| 21 | Negotiated cipher+protocol server-confirmed | `CipherMatrixOrchestratorTests`, `SchannelCipherSuiteTests` | s_server "Cipher is X" / SslStream.NegotiatedCipherSuite |
| 22 | Invalid/mismatched cipher configs fail safely | `Errors/ErrorMappingEdgeTests`, `CipherSuiteTests` (Schannel oracle), 3DES cases | SecureConnectionError |
| 23 | Certificate + hostname validation enabled | `Tls/CertificateValidationMatrixTests` | expired/untrusted/mismatch/EKU rejected |
| 24 | Resources stable under stress | `Stress/StressAndResourceTests` | +418 KB / −7 handles over 200 cycles |
| 25 | Passes on clean WS2012R2 VM | `build/validate-ws2012r2.ps1` + `docs/deployment-ws2012r2.md` | ⚠️ on-target checklist (not run in this environment) |
| 26 | Machine-readable + human-readable reports | `artifacts/` tree + `tools/CurlHttpClient.Reports` | trx/json/csv/md + verification-summary.md |
| — | SNI / ALPN | `Tls/SniAndAlpnTests`, `AlpnOracleTests` | h2 via Kestrel, http/1.1 + fallback |
| — | Supported groups / sigalgs | `Tls/GroupsAndSignaturesTests` | X25519/P-256/P-384/HRR/ML-KEM |
| — | mTLS | `Tls/MutualTlsTests` | PEM + PKCS#12 + password, 1.2/1.3 |
| — | Session resumption | `Tls/SessionResumptionTests` | curl verbose "reusing session" |
| — | Native ABI contracts | `native/CurlBridge/…/abi_test_main.cpp` | run by build scripts + CI |
| — | Malformed HTTP framing | `StatusCodes/HttpFramingTests`, `MalformedResponseTests` | HTTP/1.0, EOF, CL mismatch, bad chunks |
| — | IHttpClientFactory | `src/CurlHttpClient.DependencyInjection`, DI sample | AddCurlHttpClient + infinite lifetime |
| — | Sync HttpClient.Send | `SyncSend/SyncSendTests` | all 4 overloads + h2/async-content divergences |
