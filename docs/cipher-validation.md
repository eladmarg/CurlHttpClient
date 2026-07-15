# Cipher-suite validation matrix

Validated by `tests/CurlHttpClient.IntegrationTests/CipherSuites/` with three
independent methods (the tests assume a modern OS and an `openssl` CLI —
auto-discovered from Git for Windows or `CURLHTTP_OPENSSL_EXE` — and fail
loudly rather than skip):

1. **Pinned OpenSSL server** — `openssl s_server` restricted to exactly ONE
   suite per test (`-cipher X -tls1_2` / `-ciphersuites X -tls1_3`), so
   nothing else can be picked. The handler connects with its **default**
   configuration and the assertion reads the negotiated cipher back from the
   server's status page. ECDHE_ECDSA suites run against an ECDSA P-256
   certificate, everything else against RSA-2048; certificate verification
   stays enabled throughout (test CA).
2. **Pinned client vs the OS TLS stack (Schannel)** — the mirror image: an
   `SslStream` server on the operating system's own TLS implementation with
   its default cipher configuration, the CLIENT pinned per suite via
   `Tls12CipherList`/`Tls13CipherSuites`, and
   `SslStream.NegotiatedCipherSuite` asserted server-side. This proves
   interop with the implementation IIS/Kestrel peers run in production.
3. **ClientHello inspection** — a raw listener captures the actual ClientHello
   bytes the handler sends and parses the offered cipher-suite code points.

| Cipher suite (IANA) | Required | Protocol | Pinned OpenSSL server | OS stack (Schannel) | Offered in ClientHello |
| --- | --- | --- | --- | --- | --- |
| TLS_AES_256_GCM_SHA384 | Yes | TLS 1.3 | ✅ | ✅ | ✅ |
| TLS_AES_128_GCM_SHA256 | Yes | TLS 1.3 | ✅ | ✅ | ✅ |
| TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_DHE_RSA_WITH_AES_256_GCM_SHA384 | Yes | TLS 1.2 | ✅ | ①  | ✅ |
| TLS_DHE_RSA_WITH_AES_128_GCM_SHA256 | Yes | TLS 1.2 | ✅ | ①  | ✅ |
| TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_RSA_WITH_AES_256_GCM_SHA384 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_RSA_WITH_AES_128_GCM_SHA256 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_RSA_WITH_AES_256_CBC_SHA256 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_RSA_WITH_AES_128_CBC_SHA256 | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_RSA_WITH_AES_256_CBC_SHA | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_RSA_WITH_AES_128_CBC_SHA | Yes | TLS 1.2 | ✅ | ✅ | ✅ |
| TLS_RSA_WITH_3DES_EDE_CBC_SHA | **No** | — | ✅ correctly impossible | — | ✅ never offered (0x000A absent) |

① Current Windows 11 builds ship with **all TLS_DHE_RSA_* suites removed from
Schannel's default list** (verify: `Get-TlsCipherSuite | Where Name -like
'*DHE_RSA*'` returns nothing), so the OS itself cannot negotiate them —
that column says nothing about this library. Client-side DHE support is
proven by the pinned-OpenSSL column, and a dedicated test asserts a
DHE-pinned client fails *cleanly* (SecureConnectionError) against a
DHE-less Schannel server.

Notes:

- All "Yes" suites negotiate with the handler's **default** options — no
  cipher configuration required. TLS 1.0/1.1 variants of the CBC-SHA suites
  are unreachable regardless: the handler's floor is TLS 1.2 (not
  configurable lower).
- 3DES is validated three ways: it is not compiled into the bundled OpenSSL
  at all, a client explicitly configured with `Tls12CipherList =
  "DES-CBC3-SHA"` fails the handshake against any server
  (SecureConnectionError), and the ClientHello never contains code point
  0x000A.
- Additional coverage: `Tls12CipherList` provably pins the client side
  (server offering ALL, client restricted to one suite → that suite is
  negotiated); `MinimumTlsVersion = 1.3` removes every TLS 1.2 suite from
  the ClientHello; a stress mix runs 5 parallel requests against each of six
  differently-pinned servers (all key-exchange families) through one shared
  handler.
