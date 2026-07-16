using System.Net;

namespace CurlHttp;

/// <summary>Native execution engine for <see cref="CurlHttpMessageHandler"/>.</summary>
public enum CurlExecutionEngine
{
    /// <summary>Blocking curl_easy_perform on a bounded pool of dedicated
    /// worker threads (default). Simple and proven; each in-flight request
    /// owns a thread.</summary>
    DedicatedWorkers,

    /// <summary>A single curl_multi event-loop thread drives all transfers:
    /// connections shared across every request, HTTP/2 multiplexing, instant
    /// cancellation, no per-request thread. Backpressure is via libcurl's
    /// pause/resume. Opt-in.</summary>
    MultiEventLoop,
}

/// <summary>
/// Handler-lifetime configuration for <see cref="CurlHttpMessageHandler"/>.
/// All values are fixed at handler construction (init-only), mirroring how
/// <see cref="SocketsHttpHandler"/> freezes its options on first use.
/// </summary>
public sealed class CurlHttpClientOptions
{
    /// <summary>Explicit path (file or directory) of curl_http_bridge.dll.
    /// When null the library is resolved from <c>runtimes\win-x64\native\</c>
    /// beside the application. PATH and the current directory are never
    /// searched. Process-wide: the first handler wins.</summary>
    public string? NativeLibraryPath { get; init; }

    /// <summary>Path of a PEM CA bundle used for server certificate
    /// verification. When null, the bundled <c>cacert.pem</c> deployed next
    /// to the native bridge is used; if that is also missing,
    /// <see cref="UseSystemCertificateStore"/> must be enabled.</summary>
    public string? CertificateAuthorityBundlePath { get; init; }

    /// <summary>Additionally trust the Windows certificate store (CAs are
    /// imported into OpenSSL's verification, TLS itself stays on OpenSSL).
    /// Note that on Windows Server 2012 R2 the store's roots may be outdated,
    /// which is why the bundled CA file is the default trust source.</summary>
    public bool UseSystemCertificateStore { get; init; }

    /// <summary>Minimum negotiated TLS version. Only 1.2 (default) and 1.3
    /// are accepted; anything older is refused by this handler.</summary>
    public Version? MinimumTlsVersion { get; init; }

    /// <summary>Optional OpenSSL cipher list for TLS 1.2 and below
    /// (e.g. "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256").
    /// Leave null to use OpenSSL's secure defaults.</summary>
    public string? Tls12CipherList { get; init; }

    /// <summary>Optional TLS 1.3 cipher suites
    /// (e.g. "TLS_AES_256_GCM_SHA384:TLS_CHACHA20_POLY1305_SHA256").
    /// Leave null to use OpenSSL's secure defaults.</summary>
    public string? Tls13CipherSuites { get; init; }

    /// <summary>Client certificate file (PEM or PKCS#12) for mutual TLS.</summary>
    public string? ClientCertificatePath { get; init; }

    /// <summary>"PEM" (default) or "P12".</summary>
    public string? ClientCertificateType { get; init; }

    /// <summary>Private key file when <see cref="ClientCertificatePath"/> is a
    /// PEM certificate without an embedded key.</summary>
    public string? ClientCertificateKeyPath { get; init; }

    /// <summary>Passphrase for the client key / PKCS#12 file.</summary>
    public string? ClientCertificateKeyPassword { get; init; }

    /// <summary>Maximum time to establish the TCP/TLS connection.
    /// Zero/default lets libcurl use its own default (300 s).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional cap on the WHOLE transfer (headers + body). Null
    /// (default) leaves total-timeout ownership to <see cref="HttpClient.Timeout"/>,
    /// which already cancels via the cancellation token.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>Follow 3xx redirects natively. Matching .NET semantics,
    /// https→http downgrades are never followed.</summary>
    public bool AllowAutoRedirect { get; init; } = true;

    public int MaxAutomaticRedirections { get; init; } = 10;

    /// <summary>Advertise and transparently decode gzip/deflate/brotli.
    /// Content-Encoding/Content-Length are stripped from decoded responses,
    /// matching <see cref="HttpClientHandler.AutomaticDecompression"/>.</summary>
    public bool AutomaticDecompression { get; init; } = true;

    /// <summary>Explicit proxy. Resolved per request URI via
    /// <see cref="IWebProxy.GetProxy"/>/<see cref="IWebProxy.IsBypassed"/>.
    /// Environment proxy variables (HTTP_PROXY etc.) are NEVER honored.</summary>
    public IWebProxy? Proxy { get; init; }

    /// <summary>Master switch for <see cref="Proxy"/>. When false, or when
    /// <see cref="Proxy"/> is null, requests go direct.</summary>
    public bool UseProxy { get; init; } = true;

    /// <summary>Native execution engine. <see cref="CurlExecutionEngine.DedicatedWorkers"/>
    /// (default) runs each request on a blocking worker thread. <see
    /// cref="CurlExecutionEngine.MultiEventLoop"/> drives all transfers through
    /// one curl_multi event-loop thread: connections are shared across every
    /// request, HTTP/2 streams multiplex, cancellation is instant, and there
    /// is no per-request thread. Opt-in.</summary>
    public CurlExecutionEngine ExecutionEngine { get; init; } = CurlExecutionEngine.DedicatedWorkers;

    /// <summary>Negotiate HTTP/2 via ALPN. Off by default: with the current
    /// blocking-transfer architecture each request owns its own connection, so
    /// h2 provides no multiplexing benefit and HTTP/1.1 keep-alive pooling is
    /// usually faster. Enable for servers that require h2.</summary>
    public bool EnableHttp2 { get; init; }

    /// <summary>Enable libcurl's cookie engine scoped to a single request
    /// chain, so cookies set by a redirect hop are replayed on the next hop
    /// (SSO flows). Cookies never persist across requests. Off by default;
    /// Set-Cookie headers are always surfaced on the response either way.</summary>
    public bool EnableCookieEngineForRedirects { get; init; }

    /// <summary>Forward libcurl's verbose TEXT/HEADER diagnostics to the
    /// logger at Debug level. Authorization/Cookie header values are
    /// redacted; request/response bodies are never logged.</summary>
    public bool EnableNativeVerboseLogging { get; init; }

    /// <summary>Redact query strings when URLs are logged.</summary>
    public bool RedactQueryStringsInLogs { get; init; } = true;

    /// <summary>Maximum concurrent in-flight requests. Each in-flight request
    /// (including one whose response body is still being streamed) owns one
    /// dedicated worker thread — size this at least as large as the expected
    /// number of concurrently open response streams.
    /// Default: max(8, 2 × processor count).</summary>
    public int MaxConcurrentRequests { get; init; } = Math.Max(8, 2 * Environment.ProcessorCount);

    /// <summary>Fail fast with an <see cref="HttpRequestException"/> when no
    /// worker becomes available within this window (prevents silent
    /// queue build-up behind stuck streaming responses).</summary>
    public TimeSpan WorkerAdmissionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Best-effort cap of concurrent requests per scheme+host+port,
    /// enforced in managed code. 0 = unlimited.</summary>
    public int MaxConnectionsPerServer { get; init; }

    /// <summary>Upper bound of undelivered response-body bytes buffered
    /// between the native transfer and the consumer of the response stream.
    /// When full, the native transfer is paused by backpressure.</summary>
    public int MaxResponseBufferBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum total size, in bytes, of a response's header block
    /// (all header lines and trailers, names + values). A server exceeding
    /// this fails the transfer instead of growing managed memory without
    /// bound. Mirrors SocketsHttpHandler's MaxResponseHeadersLength intent.
    /// Default: 1 MiB.</summary>
    public int MaxResponseHeadersLength { get; init; } = 1024 * 1024;

    /// <summary>libcurl receive buffer size (bigger chunks = fewer
    /// native→managed transitions on fast downloads).</summary>
    public int ReceiveBufferSize { get; init; } = 256 * 1024;

    /// <summary>libcurl upload buffer size (CURLOPT_UPLOAD_BUFFERSIZE): bigger
    /// = fewer read-callback round trips on large uploads. Zero = libcurl
    /// default (64 KiB). libcurl clamps to 16 KiB..2 MiB.</summary>
    public int UploadBufferSize { get; init; }

    /// <summary>Idle keep-alive connections older than this are not reused
    /// (CURLOPT_MAXAGE_CONN). Zero = libcurl default (118 s).</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; init; }

    /// <summary>Connections older than this are never reused regardless of
    /// activity (CURLOPT_MAXLIFETIME_CONN); bounds DNS-failover blindness.
    /// Zero = unlimited.</summary>
    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(15);

    internal void Validate()
    {
        if (MinimumTlsVersion is not null &&
            MinimumTlsVersion != new Version(1, 2) && MinimumTlsVersion != new Version(1, 3))
        {
            throw new ArgumentException(
                $"MinimumTlsVersion must be 1.2 or 1.3 (got {MinimumTlsVersion}). " +
                "Older TLS versions are deliberately unsupported.");
        }
        // >= 1 regardless of AllowAutoRedirect: the value is applied to
        // CURLOPT_MAXREDIRS unconditionally, so a negative value would fail
        // every request even with redirects turned off.
        if (MaxAutomaticRedirections < 1)
        {
            throw new ArgumentException("MaxAutomaticRedirections must be >= 1.");
        }
        if (MaxConcurrentRequests < 1)
        {
            throw new ArgumentException("MaxConcurrentRequests must be >= 1.");
        }
        if (MaxConnectionsPerServer < 0)
        {
            throw new ArgumentException("MaxConnectionsPerServer must be >= 0 (0 = unlimited).");
        }
        if (MaxResponseBufferBytes < 64 * 1024)
        {
            throw new ArgumentException("MaxResponseBufferBytes must be at least 64 KiB.");
        }
        if (MaxResponseHeadersLength < 1024)
        {
            throw new ArgumentException("MaxResponseHeadersLength must be at least 1 KiB.");
        }
        if (ReceiveBufferSize < 16 * 1024 || ReceiveBufferSize > 10 * 1024 * 1024)
        {
            throw new ArgumentException("ReceiveBufferSize must be between 16 KiB and 10 MiB.");
        }
        // 0 = libcurl default; otherwise within libcurl's accepted 16 KiB..2 MiB.
        if (UploadBufferSize < 0 || UploadBufferSize > 2 * 1024 * 1024)
        {
            throw new ArgumentException("UploadBufferSize must be between 0 and 2 MiB.");
        }
        ValidateTimeout(ConnectTimeout, nameof(ConnectTimeout));
        if (RequestTimeout is { } requestTimeout)
        {
            ValidateTimeout(requestTimeout, nameof(RequestTimeout));
        }
        ValidateTimeout(PooledConnectionIdleTimeout, nameof(PooledConnectionIdleTimeout));
        ValidateTimeout(PooledConnectionLifetime, nameof(PooledConnectionLifetime));
        if (ClientCertificateType is not null and not "PEM" and not "P12")
        {
            throw new ArgumentException("ClientCertificateType must be \"PEM\" or \"P12\".");
        }
        if (CertificateAuthorityBundlePath is not null && !File.Exists(CertificateAuthorityBundlePath))
        {
            throw new FileNotFoundException(
                "CertificateAuthorityBundlePath does not exist.", CertificateAuthorityBundlePath);
        }
    }

    /// <summary>Rejects timeouts that would be lost crossing to libcurl's
    /// 32-bit <c>long</c> options on Win64. Zero and Timeout.InfiniteTimeSpan
    /// mean "no timeout" (libcurl default) and are allowed.</summary>
    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value == Timeout.InfiniteTimeSpan || value == TimeSpan.Zero)
        {
            return;
        }
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must not be negative (use Timeout.InfiniteTimeSpan for no timeout).");
        }
        if (value.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentException(
                $"{name} must be at most {TimeSpan.FromMilliseconds(int.MaxValue).TotalDays:F0} days " +
                "(libcurl's timeout is a 32-bit millisecond value).");
        }
    }
}
