using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using CurlHttp.Diagnostics;
using CurlHttp.Internal;
using CurlHttp.Native;
using Microsoft.Extensions.Logging;

namespace CurlHttp;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that executes HTTP/HTTPS requests
/// through a bundled native libcurl + OpenSSL bridge instead of the OS TLS
/// stack. Built for hosts whose Schannel cannot negotiate modern TLS
/// (Windows Server 2012 R2), while application code keeps using HttpClient.
///
/// Lifetime: create ONE handler per distinct configuration and keep it for
/// the application lifetime (like SocketsHttpHandler). The handler is safe
/// for unlimited concurrent SendAsync calls. When used with
/// IHttpClientFactory, set the handler lifetime to infinite — connection
/// staleness is bounded by <see cref="CurlHttpClientOptions.PooledConnectionLifetime"/>,
/// so factory recycling only churns native resources.
/// </summary>
public sealed class CurlHttpMessageHandler : HttpMessageHandler
{
    private readonly CurlHttpClientOptions _options;
    private readonly ILogger? _logger;
    private readonly SafeHandle _nativeClient; // CurlBridgeClientHandle or CurlBridgeMultiClientHandle
    private readonly ICurlDispatcher _dispatcher;
    private readonly string _trustSource;
    private int _disposeGuard;
    private volatile bool _disposed;

    public CurlHttpMessageHandler(CurlHttpClientOptions options, ILogger<CurlHttpMessageHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _logger = logger;

        NativeLibraryLoader.Configure(options.NativeLibraryPath);

        // First P/Invoke: loads the DLL through the controlled resolver and
        // initializes libcurl exactly once per process.
        CurlBridgeResult init;
        try
        {
            init = NativeMethods.GlobalInitialize();
        }
        catch (DllNotFoundException ex)
        {
            throw new CurlHttpInitializationException(ex.Message, ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new CurlHttpInitializationException(
                "curl_http_bridge.dll could not be loaded — architecture mismatch " +
                $"(process is {RuntimeInformation.ProcessArchitecture}, the DLL is x64).", ex);
        }
        if (init != CurlBridgeResult.Ok)
        {
            throw new CurlHttpInitializationException(
                $"Native bridge initialization failed ({init}): {NativeMethods.GetLastGlobalError()}");
        }

        // Fail fast unless the linked libcurl is the build this library requires.
        CurlHttpClientDiagnostics probe = CurlHttpClientDiagnostics.Parse(
            NativeMethods.GetVersionInfoJson(), NativeLibraryLoader.LoadedPath, "(probe)");
        if (!probe.TlsBackendIsOpenSsl)
        {
            throw new CurlHttpInitializationException(
                $"The libcurl TLS backend is '{probe.SslVersion}', not OpenSSL. This defeats the purpose " +
                "of this handler (modern TLS independent of Schannel) — refusing to start.");
        }
        if (!probe.SupportsAsyncDns)
        {
            throw new CurlHttpInitializationException(
                "libcurl lacks the threaded resolver (AsynchDNS); cancellation during connects would hang.");
        }

        string? caBundlePath = LoadCaBundle(out _trustSource);
        bool multi = options.ExecutionEngine == CurlExecutionEngine.MultiEventLoop;
        _nativeClient = CreateNativeClient(options, caBundlePath, multi);
        _dispatcher = multi
            ? new CurlMultiDispatcher((CurlBridgeMultiClientHandle)_nativeClient, options)
            : new CurlRequestDispatcher((CurlBridgeClientHandle)_nativeClient, options);

        CurlHttpEventSource.Log.HandlerStarted(
            probe.CurlVersion, probe.SslVersion, NativeLibraryLoader.LoadedPath ?? string.Empty);
        _logger?.LogInformation(
            "CurlHttpMessageHandler started: curl {CurlVersion}, TLS {SslVersion}, trust {TrustSource}, " +
            "bridge at {NativeLibraryPath}",
            probe.CurlVersion, probe.SslVersion, _trustSource, NativeLibraryLoader.LoadedPath);
    }

    /// <summary>Runtime proof of what the native stack actually is. Health
    /// checks should assert <see cref="CurlHttpClientDiagnostics.TlsBackendIsOpenSsl"/>.</summary>
    public CurlHttpClientDiagnostics GetDiagnostics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CurlHttpClientDiagnostics.Parse(
            NativeMethods.GetVersionInfoJson(), NativeLibraryLoader.LoadedPath, _trustSource);
    }

    /// <summary>
    /// Synchronous send. Blocking here is safe by construction: the transfer
    /// itself runs on a dedicated (non-ThreadPool) worker thread, and every
    /// completion source in the pipeline runs its continuations
    /// asynchronously, so completion never depends on the blocked caller.
    ///
    /// Notes vs SocketsHttpHandler: this handler supports synchronous HTTP/2
    /// (SocketsHttpHandler throws), and request content that only implements
    /// asynchronous serialization is still streamed correctly (the body is
    /// pumped from the worker thread, not the caller). Many concurrent
    /// synchronous sends issued FROM ThreadPool threads can still starve the
    /// pool exactly as with any sync-over-async usage — prefer SendAsync in
    /// highly concurrent paths.
    /// </summary>
    protected override HttpResponseMessage Send(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        Uri uri = request.RequestUri
            ?? throw new InvalidOperationException("HttpRequestMessage.RequestUri must be set.");
        if (!uri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("HttpRequestMessage.RequestUri must be absolute.");
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new NotSupportedException(
                $"Only http and https are supported (got '{uri.Scheme}').");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var scope = new CurlHttpEventSourceScope(_logger, request, _options.RedactQueryStringsInLogs);
        (string proxy, string? proxyUserPassword) = ResolveProxy(uri);
        // With proxy credentials configured, a 407 header block may be an
        // intermediate hop (libcurl retries with Proxy-Authorization); the
        // parser must not finalize it eagerly.
        var context = new CurlRequestContext(request, _options, cancellationToken, scope,
            proxyAuthRetryPossible: proxyUserPassword is not null);
        bool dispatched = false;
        try
        {
            await context.PrepareRequestBodyAsync(cancellationToken).ConfigureAwait(false);
            var plan = new NativeRequestPlan(
                Method: request.Method.Method,
                Url: uri.AbsoluteUri,
                HeaderLines: BuildHeaderLines(request),
                Proxy: proxy,
                ProxyUserPassword: proxyUserPassword,
                // .NET parity: an https request never downgrades to http on redirect.
                RedirectProtocols: uri.Scheme == Uri.UriSchemeHttps ? "https" : "http,https",
                ContentLength: context.ContentLength);

            context.RegisterCallerCancellation();
            await _dispatcher.DispatchAsync(context, plan, cancellationToken).ConfigureAwait(false);
            dispatched = true;

            return await context.ResponseTask.ConfigureAwait(false);
        }
        finally
        {
            if (!dispatched)
            {
                // The worker never ran; reclaim what SendAsync allocated.
                context.OnWorkerFinished();
                context.Dispose();
            }
        }
    }

    /* ------------------------- request translation ------------------------- */

    private static List<string> BuildHeaderLines(HttpRequestMessage request)
    {
        var lines = new List<string>();

        foreach (KeyValuePair<string, HeaderStringValues> header in request.Headers.NonValidated)
        {
            if (IsManagedNatively(header.Key))
            {
                continue;
            }
            foreach (string value in header.Value)
            {
                lines.Add(FormatHeaderLine(header.Key, value));
            }
        }

        if (request.Content is not null)
        {
            foreach (KeyValuePair<string, HeaderStringValues> header in request.Content.Headers.NonValidated)
            {
                // Content-Length is owned by libcurl (set from the body plan);
                // a mismatched duplicate would corrupt framing.
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (string value in header.Value)
                {
                    lines.Add(FormatHeaderLine(header.Key, value));
                }
            }
        }

        // libcurl adds "Expect: 100-continue" on its own for sizable uploads
        // and waits up to a second for the interim response; SocketsHttpHandler
        // does not. Policy: explicit opt-in via ExpectContinue==true always
        // sends the header (regardless of body size); anything else suppresses
        // libcurl's automatic behavior.
        lines.Add(request.Headers.ExpectContinue == true
            ? "Expect: 100-continue"
            : "Expect:");
        return lines;
    }

    private static bool IsManagedNatively(string headerName)
    {
        // Transfer-Encoding: chunked is applied natively when the body length
        // is unknown; Expect has an explicit policy below.
        return headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Expect", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatHeaderLine(string name, string value)
    {
        // libcurl conventions: "Name;" sends an empty value, bare "Name:"
        // suppresses a header. Escape the empty-value case accordingly.
        return string.IsNullOrEmpty(value) ? name + ";" : $"{name}: {value}";
    }

    private (string Proxy, string? UserPassword) ResolveProxy(Uri uri)
    {
        // "" disables proxying INCLUDING libcurl's environment-variable
        // lookup — proxy behaviour must always come from these options.
        if (!_options.UseProxy || _options.Proxy is null || _options.Proxy.IsBypassed(uri))
        {
            return (string.Empty, null);
        }
        Uri? proxyUri = _options.Proxy.GetProxy(uri);
        if (proxyUri is null)
        {
            return (string.Empty, null);
        }

        string? userPassword = null;
        NetworkCredential? credential = _options.Proxy.Credentials?.GetCredential(proxyUri, "Basic");
        if (credential is not null && !string.IsNullOrEmpty(credential.UserName))
        {
            string user = string.IsNullOrEmpty(credential.Domain)
                ? credential.UserName
                : $"{credential.Domain}\\{credential.UserName}";
            userPassword = $"{user}:{credential.Password}";
        }
        return (proxyUri.AbsoluteUri, userPassword);
    }

    /* ------------------------- native client setup ------------------------- */

    /// <summary>Resolves the CA bundle FILE path (not bytes): passing a path
    /// to libcurl lets OpenSSL cache the parsed X509 store across new
    /// connections, whereas an in-memory blob is re-parsed on every TLS
    /// handshake. Returns null when only the Windows store is trusted.</summary>
    private string? LoadCaBundle(out string trustSource)
    {
        if (_options.CertificateAuthorityBundlePath is { } explicitPath)
        {
            string full = Path.GetFullPath(explicitPath);
            // An empty bundle provides no trust source; since verification can
            // never be disabled, refuse to start rather than pass an empty
            // file to libcurl (which would only fail later, per connection).
            if (new FileInfo(full).Length == 0)
            {
                throw new CurlHttpInitializationException(
                    $"The configured CA bundle '{full}' is empty — no trust source is available " +
                    "and certificate verification cannot be disabled, so the handler refuses to start.");
            }
            trustSource = full;
            return full;
        }

        // Default: the cacert.pem deployed beside the native bridge.
        string? nativeDir = Path.GetDirectoryName(NativeLibraryLoader.LoadedPath);
        if (nativeDir is not null)
        {
            string bundled = Path.Combine(nativeDir, "cacert.pem");
            if (File.Exists(bundled))
            {
                trustSource = _options.UseSystemCertificateStore
                    ? bundled + " + windows certificate store"
                    : bundled;
                return bundled;
            }
        }

        if (_options.UseSystemCertificateStore)
        {
            trustSource = "windows certificate store";
            return null;
        }

        throw new CurlHttpInitializationException(
            "No certificate trust source is available: no CertificateAuthorityBundlePath was " +
            "configured, no cacert.pem was found beside curl_http_bridge.dll, and " +
            "UseSystemCertificateStore is disabled. Certificate verification cannot be skipped, " +
            "so the handler refuses to start.");
    }

    /// <summary>Escape hatch: when the CURLHTTP_CA_BLOB environment variable
    /// is "1", the CA bundle is passed to libcurl as an in-memory blob rather
    /// than a file path. This disables OpenSSL's cross-connection X509-store
    /// cache (slower new connections) and exists only as a diagnostic/measure
    /// tool and a fallback should a future libcurl regress path caching.</summary>
    private static bool UseCaBlob =>
        Environment.GetEnvironmentVariable("CURLHTTP_CA_BLOB") == "1";

    private static unsafe SafeHandle CreateNativeClient(
        CurlHttpClientOptions options, string? caBundlePath, bool multi)
    {
        var strings = new List<IntPtr>();
        IntPtr Utf8(string? value)
        {
            if (value is null)
            {
                return IntPtr.Zero;
            }
            IntPtr ptr = Marshal.StringToCoTaskMemUTF8(value);
            strings.Add(ptr);
            return ptr;
        }

        byte[] caBlob = UseCaBlob && caBundlePath is not null
            ? File.ReadAllBytes(caBundlePath)
            : [];
        fixed (byte* caPtr = caBlob)
        {
            try
            {
                var native = new BridgeClientOptionsNative
                {
                    StructSize = (uint)Marshal.SizeOf<BridgeClientOptionsNative>(),
                    CaBundlePath = UseCaBlob ? IntPtr.Zero : Utf8(caBundlePath),
                    CaBundlePem = (IntPtr)caPtr,
                    CaBundlePemLength = (ulong)caBlob.Length,
                    UseNativeCa = options.UseSystemCertificateStore ? 1 : 0,
                    MinTlsVersion = options.MinimumTlsVersion?.Minor == 3 ? 13 : 12,
                    Tls12CipherList = Utf8(options.Tls12CipherList),
                    Tls13CipherSuites = Utf8(options.Tls13CipherSuites),
                    ClientCertPath = Utf8(options.ClientCertificatePath),
                    ClientCertType = Utf8(options.ClientCertificateType),
                    ClientKeyPath = Utf8(options.ClientCertificateKeyPath),
                    ClientKeyPassword = Utf8(options.ClientCertificateKeyPassword),
                    ConnectTimeoutMs = (long)options.ConnectTimeout.TotalMilliseconds,
                    RequestTimeoutMs = (long)(options.RequestTimeout?.TotalMilliseconds ?? 0),
                    FollowRedirects = options.AllowAutoRedirect ? 1 : 0,
                    MaxRedirects = options.MaxAutomaticRedirections,
                    EnableDecompression = options.AutomaticDecompression ? 1 : 0,
                    EnableHttp2 = options.EnableHttp2 ? 1 : 0,
                    EnableCookieEngine = options.EnableCookieEngineForRedirects ? 1 : 0,
                    Verbose = options.EnableNativeVerboseLogging ? 1 : 0,
                    BufferSize = options.ReceiveBufferSize,
                    UploadBufferSize = options.UploadBufferSize,
                    MaxEasyHandles = Math.Min(options.MaxConcurrentRequests, 16),
                    ConnectionIdleTimeoutSecs = (long)options.PooledConnectionIdleTimeout.TotalSeconds,
                    ConnectionMaxLifetimeSecs = (long)options.PooledConnectionLifetime.TotalSeconds,
                };

                if (multi)
                {
                    CurlBridgeMultiClientHandle multiClient = NativeMethods.MultiCreate(in native);
                    if (multiClient.IsInvalid)
                    {
                        throw new CurlHttpInitializationException(
                            $"Native event-loop client creation failed: {NativeMethods.GetLastGlobalError()}");
                    }
                    return multiClient;
                }
                CurlBridgeClientHandle client = NativeMethods.ClientCreate(in native);
                if (client.IsInvalid)
                {
                    throw new CurlHttpInitializationException(
                        $"Native client creation failed: {NativeMethods.GetLastGlobalError()}");
                }
                return client;
            }
            finally
            {
                foreach (IntPtr ptr in strings)
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        // Interlocked guard: two concurrent Dispose calls must not both run the
        // teardown (double CompleteAdding / double native destroy).
        if (disposing && Interlocked.Exchange(ref _disposeGuard, 1) == 0)
        {
            _disposed = true;
            // Order matters: stop admission and drain workers/loop first, then
            // the native client (whose destroy insists on zero active requests
            // / joins the loop thread).
            _dispatcher.Dispose();
            _nativeClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
