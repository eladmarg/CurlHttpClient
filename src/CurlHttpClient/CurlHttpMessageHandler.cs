using System.Net;
using System.Net.Http;
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
    private readonly CurlBridgeClientHandle _client;
    private readonly CurlRequestDispatcher _dispatcher;
    private readonly string _trustSource;
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

        byte[] caBundle = LoadCaBundle(out _trustSource);
        _client = CreateNativeClient(options, caBundle);
        _dispatcher = new CurlRequestDispatcher(_client, options);

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
        var context = new CurlRequestContext(request, _options, cancellationToken, scope);
        bool dispatched = false;
        try
        {
            await context.PrepareRequestBodyAsync(cancellationToken).ConfigureAwait(false);

            (string proxy, string? proxyUserPassword) = ResolveProxy(uri);
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
        // does not. Suppress unless the caller opted in.
        if (request.Headers.ExpectContinue != true)
        {
            lines.Add("Expect:");
        }
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

    private byte[] LoadCaBundle(out string trustSource)
    {
        if (_options.CertificateAuthorityBundlePath is { } explicitPath)
        {
            trustSource = Path.GetFullPath(explicitPath);
            return File.ReadAllBytes(explicitPath);
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
                return File.ReadAllBytes(bundled);
            }
        }

        if (_options.UseSystemCertificateStore)
        {
            trustSource = "windows certificate store";
            return [];
        }

        throw new CurlHttpInitializationException(
            "No certificate trust source is available: no CertificateAuthorityBundlePath was " +
            "configured, no cacert.pem was found beside curl_http_bridge.dll, and " +
            "UseSystemCertificateStore is disabled. Certificate verification cannot be skipped, " +
            "so the handler refuses to start.");
    }

    private static unsafe CurlBridgeClientHandle CreateNativeClient(
        CurlHttpClientOptions options, byte[] caBundle)
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

        fixed (byte* caPtr = caBundle)
        {
            try
            {
                var native = new BridgeClientOptionsNative
                {
                    StructSize = (uint)Marshal.SizeOf<BridgeClientOptionsNative>(),
                    CaBundlePem = (IntPtr)caPtr,
                    CaBundlePemLength = (ulong)caBundle.Length,
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
                    MaxEasyHandles = Math.Min(options.MaxConcurrentRequests, 16),
                    ConnectionIdleTimeoutSecs = (long)options.PooledConnectionIdleTimeout.TotalSeconds,
                    ConnectionMaxLifetimeSecs = (long)options.PooledConnectionLifetime.TotalSeconds,
                };

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
        if (disposing && !_disposed)
        {
            _disposed = true;
            // Order matters: stop admission and drain workers first, then the
            // native client (whose destroy insists on zero active requests).
            _dispatcher.Dispose();
            _client.Dispose();
        }
        base.Dispose(disposing);
    }
}
