using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using CurlHttp.Native;

namespace CurlHttp.Internal;

/// <summary>
/// Bridge result -> .NET exception mapping, following HttpClient conventions:
///  - caller cancellation  -> TaskCanceledException carrying the caller token
///  - total request timeout -> TaskCanceledException with TimeoutException inner
///  - connect timeout       -> HttpRequestException(ConnectionError) w/ TimeoutException inner
///  - TLS / certificate     -> HttpRequestException(SecureConnectionError) w/ AuthenticationException inner
///  - DNS                   -> HttpRequestException(NameResolutionError)
///  - everything protocol   -> HttpRequestException(InvalidResponse)
/// Native detail (curl error code + libcurl error buffer) is preserved in the
/// message and in Exception.Data["CurlErrorCode"] — never any credentials.
/// </summary>
internal static class CurlErrorMapper
{
    public static Exception Map(
        CurlBridgeResult result,
        int curlErrorCode,
        string nativeError,
        Exception? callbackException,
        CancellationToken? callerToken,
        bool timedOut,
        bool streamDisposed)
    {
        string detail = nativeError.Length > 0 ? nativeError : $"curl bridge result {result}";

        Exception exception = result switch
        {
            CurlBridgeResult.Cancelled when streamDisposed =>
                new ObjectDisposedException(nameof(CurlResponseStream),
                    "The response stream was disposed; the transfer was aborted."),

            CurlBridgeResult.Cancelled when callerToken is { } token =>
                new TaskCanceledException("The request was canceled.", null, token),

            CurlBridgeResult.Cancelled =>
                new TaskCanceledException("The request was canceled."),

            CurlBridgeResult.Timeout =>
                new TaskCanceledException(
                    $"The request was canceled because the configured RequestTimeout elapsed. {detail}",
                    new TimeoutException(detail)),

            CurlBridgeResult.ConnectTimeout =>
                new HttpRequestException(HttpRequestError.ConnectionError,
                    $"A connection could not be established within the configured ConnectTimeout. {detail}",
                    new TimeoutException(detail)),

            CurlBridgeResult.CertError =>
                new HttpRequestException(HttpRequestError.SecureConnectionError,
                    $"The remote certificate or host name was rejected. {detail}",
                    new AuthenticationException(detail)),

            CurlBridgeResult.TlsError =>
                new HttpRequestException(HttpRequestError.SecureConnectionError,
                    $"The TLS handshake failed. {detail}",
                    new AuthenticationException(detail)),

            CurlBridgeResult.DnsError =>
                new HttpRequestException(HttpRequestError.NameResolutionError,
                    $"The host name could not be resolved. {detail}"),

            CurlBridgeResult.ConnectError =>
                new HttpRequestException(HttpRequestError.ConnectionError,
                    $"The connection could not be established. {detail}"),

            CurlBridgeResult.NetworkError =>
                new HttpRequestException(HttpRequestError.ResponseEnded,
                    $"The connection failed during the transfer. {detail}"),

            CurlBridgeResult.ProtocolError =>
                new HttpRequestException(HttpRequestError.InvalidResponse,
                    $"The server sent an invalid or unexpected response. {detail}"),

            CurlBridgeResult.TooManyRedirects =>
                new HttpRequestException(HttpRequestError.Unknown,
                    $"The maximum number of automatic redirections was exceeded. {detail}"),

            CurlBridgeResult.CallbackError =>
                callbackException as HttpRequestException ??
                new HttpRequestException(
                    $"A request/response callback failed. {detail}", callbackException),

            CurlBridgeResult.InvalidArgument =>
                new HttpRequestException(HttpRequestError.Unknown,
                    $"libcurl rejected the request. {detail}"),

            CurlBridgeResult.Unsupported =>
                new CurlHttpInitializationException(
                    $"The native bridge lacks a required capability. {detail}"),

            _ =>
                new HttpRequestException(HttpRequestError.Unknown,
                    $"The native transfer failed. {detail}", callbackException),
        };

        // Timeout that raced ahead of a caller token which is also canceled:
        // caller intent wins (handled above via Cancelled), nothing to do here.
        _ = timedOut;

        if (curlErrorCode != 0)
        {
            exception.Data["CurlErrorCode"] = curlErrorCode;
        }
        exception.Data["CurlBridgeResult"] = result.ToString();
        return exception;
    }
}

/// <summary>Thrown when the native bridge or its libcurl build cannot be
/// initialized (missing DLL, wrong architecture, wrong TLS backend).</summary>
public sealed class CurlHttpInitializationException : InvalidOperationException
{
    public CurlHttpInitializationException(string message) : base(message)
    {
    }

    public CurlHttpInitializationException(string message, Exception inner) : base(message, inner)
    {
    }
}
