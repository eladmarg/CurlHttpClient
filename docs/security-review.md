# Security review

## TLS posture

- `CURLOPT_SSL_VERIFYPEER=1` and `CURLOPT_SSL_VERIFYHOST=2` are set
  unconditionally. **There is no public option to disable peer or hostname
  verification**, by design. The native client refuses to be created with no
  trust source at all.
- Minimum TLS version is 1.2 (`CURL_SSLVERSION_TLSv1_2` floor); options only
  allow raising it to 1.3. TLS 1.0/1.1 cannot be enabled.
- Cipher configuration (`Tls12CipherList`, `Tls13CipherSuites`) is optional
  and validated eagerly at handler construction (a bad string fails startup,
  not the first request). Defaults are OpenSSL's secure defaults.
- The TLS backend is pinned to OpenSSL with `curl_global_sslset` **before**
  `curl_global_init`, defeating the `CURL_SSL_BACKEND` environment variable;
  the handler additionally fail-fasts unless `curl_version_info` reports an
  active OpenSSL backend. (The build is MultiSSL because the vcpkg curl
  port's `http2` feature drags Schannel in as an alternate backend — pinned
  away, it is dead code.)

## Trust sources

- Default: the vendored Mozilla `cacert.pem` deployed beside the bridge,
  loaded into memory and passed via `CURLOPT_CAINFO_BLOB` (path-tamper
  window minimized; the file is read once at handler construction).
- `CertificateAuthorityBundlePath`: explicit PEM bundle (validated to exist
  at option validation).
- `UseSystemCertificateStore`: opt-in `CURLSSLOPT_NATIVE_CA` — off by default
  because 2012 R2 machines frequently have stale root stores.
- Client certificates: file paths only (PEM/PKCS#12); the key passphrase is
  marshaled to the native side once and never logged.

## Native library loading

- Resolution is a custom `DllImportResolver`: an explicitly configured
  absolute path, else `runtimes/win-x64/native/` relative to the managed
  assembly / AppContext.BaseDirectory. **PATH and the current working
  directory are never searched**, so a planted `curl_http_bridge.dll` (or an
  unrelated `libcurl.dll` — the name is deliberately unique) cannot be
  loaded.
- Architecture is validated first with a clear x86/x64 error message.
- The module is pinned for process lifetime (never `FreeLibrary`'d), which is
  the only safe policy with a statically linked OpenSSL (atexit handlers).
- `build/package.cmd` emits SHA-256 hashes of the native assets;
  `build/check-imports.ps1` gates the build on the Win 8.1 import allowlist.

## Request/response hygiene

- Proxy behaviour comes exclusively from `CurlHttpClientOptions.Proxy`; the
  bridge always sets `CURLOPT_PROXY` explicitly (`""` disables), so libcurl's
  `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` environment-variable lookup can
  never silently reroute traffic.
- Protocols are locked to `http,https` (`CURLOPT_PROTOCOLS_STR`); redirects
  from https cannot downgrade to http (`CURLOPT_REDIR_PROTOCOLS_STR`),
  matching .NET.
- On cross-host/scheme/port redirects libcurl strips `Authorization` and
  `Cookie` (`CURLOPT_UNRESTRICTED_AUTH` unset). ⚠️ Other sensitive custom
  headers (e.g. `X-Api-Key`) ARE forwarded to redirect targets — same as
  libcurl/most clients; avoid custom credential headers with
  `AllowAutoRedirect` if targets can redirect off-origin.
- Custom headers are sent to the origin only, never on proxy CONNECT
  (`CURLHEADER_SEPARATE`); proxy CONNECT response headers are suppressed
  (`CURLOPT_SUPPRESS_CONNECT_HEADERS`).
- Cookies: no engine by default. The opt-in per-request-chain engine purges
  with `CURLOPT_COOKIELIST("ALL")` before every pooled-handle reuse
  (`curl_easy_reset` does NOT clear cookies), so no cookie can leak across
  requests.

## Logging and secrets

- Verbose native output is opt-in; the bridge forwards only TEXT and
  header lines — request/response **bodies and raw TLS payloads never leave
  the native layer**.
- The managed logger redacts `Authorization`, `Proxy-Authorization`,
  `Cookie`, `Set-Cookie`, `X-Api-Key` header values, and (by default) query
  strings in logged URLs.
- Exceptions carry the libcurl error buffer (connection/TLS detail), which
  does not contain request headers or bodies. Proxy credentials are never
  included in any message.

## Update/CVE process

Static linking means Windows Update does not patch this stack — see
docs/versions.md for the rebuild pipeline. Deployed versions are auditable at
runtime via `GetDiagnostics()` / the `CurlHttpClient` EventSource
`HandlerStarted` event.

## Known accepted risks

1. .NET 10 on WS2012R2 is unsupported by Microsoft (explicit project
   decision; mitigations in docs/deployment-ws2012r2.md).
2. Schannel code is present-but-pinned-away in the DLL (MultiSSL build).
3. Proxy credentials transit the ABI as a UTF-8 string
   (`CURLOPT_PROXYUSERPWD`) held in native memory for the request duration —
   same exposure class as every native HTTP client.
