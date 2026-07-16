namespace CurlHttp.Internal;

/// <summary>Engine-agnostic dispatch surface used by
/// <see cref="CurlHttpMessageHandler"/>. Implemented by the dedicated-worker
/// engine (<see cref="CurlRequestDispatcher"/>) and the curl_multi event-loop
/// engine (<see cref="CurlMultiDispatcher"/>).</summary>
internal interface ICurlDispatcher : IDisposable
{
    Task DispatchAsync(CurlRequestContext context, NativeRequestPlan plan, CancellationToken cancellationToken);
}
