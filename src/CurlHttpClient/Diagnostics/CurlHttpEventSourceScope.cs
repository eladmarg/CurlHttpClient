using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CurlHttp.Diagnostics;

/// <summary>Per-request diagnostics scope: one place that fans out to the
/// EventSource and the optional ILogger, with redaction applied once.</summary>
internal sealed class CurlHttpEventSourceScope
{
    private readonly ILogger? _logger;
    private readonly string _method;
    private readonly string _redactedUrl;
    private readonly long _startTimestamp;

    public CurlHttpEventSourceScope(ILogger? logger, HttpRequestMessage request, bool redactQuery)
    {
        _logger = logger;
        _method = request.Method.Method;
        _redactedUrl = LogRedaction.RedactUrl(request.RequestUri!, redactQuery);
        _startTimestamp = Stopwatch.GetTimestamp();

        CurlHttpEventSource.Log.RequestStart(_method, _redactedUrl);
        _logger?.LogDebug("curl request starting: {Method} {Url}", _method, _redactedUrl);
    }

    private double ElapsedMs => Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    public void OnHeadersReceived(int statusCode)
    {
        _logger?.LogDebug("curl response headers: {Method} {Url} -> {StatusCode} after {ElapsedMs:F1} ms",
            _method, _redactedUrl, statusCode, ElapsedMs);
    }

    public void OnCompleted(int statusCode, int httpVersion, bool connectionReused,
        long totalTimeUs, int redirectCount)
    {
        CurlHttpEventSource.Log.RequestStop(_method, _redactedUrl, statusCode, ElapsedMs,
            httpVersion, connectionReused);
        _logger?.LogInformation(
            "curl request completed: {Method} {Url} -> {StatusCode} (HTTP/{HttpVersion}, " +
            "{ElapsedMs:F1} ms, curl {CurlTotalMs:F1} ms, reused={ConnectionReused}, redirects={Redirects})",
            _method, _redactedUrl, statusCode, httpVersion / 10.0, ElapsedMs,
            totalTimeUs / 1000.0, connectionReused, redirectCount);
    }

    public void OnFailed(string bridgeResult, int curlErrorCode, string detail)
    {
        CurlHttpEventSource.Log.RequestFailed(_method, _redactedUrl, bridgeResult, curlErrorCode, ElapsedMs);
        _logger?.LogWarning(
            "curl request failed: {Method} {Url} -> {BridgeResult} (curl code {CurlErrorCode}, " +
            "{ElapsedMs:F1} ms): {Detail}",
            _method, _redactedUrl, bridgeResult, curlErrorCode, ElapsedMs, detail);
    }

    public void OnNativeDebug(int kind, ReadOnlySpan<byte> data)
    {
        if (_logger is null || !_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }
        string text = Encoding.Latin1.GetString(data).TrimEnd('\r', '\n');
        if (text.Length == 0)
        {
            return;
        }
        string prefix = kind switch
        {
            1 => "<<",
            2 => ">>",
            _ => "**",
        };
        _logger.LogDebug("curl {Prefix} {Line}", prefix, LogRedaction.RedactHeaderLine(text));
    }
}
