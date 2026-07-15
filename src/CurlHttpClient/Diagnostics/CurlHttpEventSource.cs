using System.Diagnostics.Tracing;

namespace CurlHttp.Diagnostics;

/// <summary>ETW/EventPipe counters and events for the handler.
/// Listen with: dotnet-trace collect --providers CurlHttpClient.</summary>
[EventSource(Name = "CurlHttpClient")]
internal sealed class CurlHttpEventSource : EventSource
{
    public static readonly CurlHttpEventSource Log = new();

    private CurlHttpEventSource()
    {
    }

    [Event(1, Level = EventLevel.Informational)]
    public void RequestStart(string method, string url)
    {
        if (IsEnabled())
        {
            WriteEvent(1, method, url);
        }
    }

    [Event(2, Level = EventLevel.Informational)]
    public void RequestStop(string method, string url, int statusCode, double durationMs,
        int httpVersion, bool connectionReused)
    {
        if (IsEnabled())
        {
            WriteEvent(2, method, url, statusCode, durationMs, httpVersion, connectionReused);
        }
    }

    [Event(3, Level = EventLevel.Warning)]
    public void RequestFailed(string method, string url, string bridgeResult, int curlErrorCode,
        double durationMs)
    {
        if (IsEnabled())
        {
            WriteEvent(3, method, url, bridgeResult, curlErrorCode, durationMs);
        }
    }

    [Event(4, Level = EventLevel.Informational)]
    public void HandlerStarted(string curlVersion, string sslVersion, string nativeLibraryPath)
    {
        if (IsEnabled())
        {
            WriteEvent(4, curlVersion, sslVersion, nativeLibraryPath);
        }
    }
}
